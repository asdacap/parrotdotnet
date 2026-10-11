using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parrot.Llm.Wire;

// Adapts the Anthropic Messages wire protocol to the neutral LLMEvent stream.
// The completed message's content blocks, thinking and signatures included, are
// kept as provider content and replayed verbatim to the provider that produced them.
internal static class AnthropicMessagesAdapter
{
    private static readonly JsonElement EmptyObject = JsonElement.Parse("{}");
    private static readonly CacheControl Ephemeral = new();

    public static byte[] Encode(LLMRequest request, string providerId)
    {
        var system = new List<ContentBlock>();
        if (request.Instructions.Length > 0)
        {
            system.Add(ContentBlock.TextBlock(request.Instructions));
        }

        var messages = new List<MessageParam>();
        foreach (var message in request.Messages)
        {
            if (message.Role == LLMRole.System && messages.Count == 0)
            {
                system.AddRange(TextBlocks(message.Content));
                continue;
            }

            var role = message.Role == LLMRole.Assistant ? "assistant" : "user";
            var blocks = message.Role == LLMRole.Assistant && message.ProviderContent.IsFrom(providerId)
                ? ReplayedBlocks(message)
                : Blocks(message);
            if (blocks.Count == 0)
            {
                continue;
            }

            // Tool results and system updates arrive as separate messages but
            // must share one user turn.
            if (messages.Count > 0 && messages[^1].Role == role)
            {
                messages[^1] = new MessageParam { Role = role, Content = [.. messages[^1].Content, .. blocks] };
            }
            else
            {
                messages.Add(new MessageParam { Role = role, Content = blocks });
            }
        }

        if (system.Count > 0)
        {
            system[^1] = system[^1].Cached();
        }

        var effort = request.Reasoning?.Effort ?? string.Empty;
        var replaysThinking = messages.Any(message =>
            message.Content.Any(block => block.Type is "thinking" or "redacted_thinking"));
        var body = new Body
        {
            Model = request.Model,
            MaxTokens = request.MaxOutputTokens,
            System = system.Count > 0 ? system : null,
            Messages = messages,
            Tools = request.Tools.Count == 0
                ? null
                : [.. request.Tools.Select(tool => new ToolParam
                {
                    Name = tool.Name,
                    Description = tool.Description.Length > 0 ? tool.Description : null,
                    InputSchema = WireEncoding.ParseSchema(tool.ParametersJson),
                })],
            Thinking = effort.Length > 0 || replaysThinking ? new Thinking() : null,
            OutputConfig = effort.Length > 0 ? new OutputConfig { Effort = effort } : null,
        };

        return JsonSerializer.SerializeToUtf8Bytes(body, WireJsonContext.Default.AnthropicMessagesBody);
    }

    public static async IAsyncEnumerable<LLMEvent> Parse(
        Stream stream,
        int maxEventBytes,
        string providerId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var state = new ParseState();

        await foreach (var record in SseDecoder.Decode(stream, maxEventBytes, cancellationToken).ConfigureAwait(false))
        {
            foreach (var published in Consume(record.Data, state))
            {
                yield return published;
            }

            if (state.Stopped)
            {
                break;
            }
        }

        if (!state.Stopped)
        {
            throw new WireProtocolException("anthropic: unexpected provider EOF (stream ended without message_stop)");
        }

        foreach (var toolCall in state.ToolCalls())
        {
            yield return LLMEvent.ToolCallDelta(toolCall.Id, toolCall.Name, toolCall.ArgumentsJson);
        }

        yield return LLMEvent.Completed(
            state.FinishReason,
            state.InputTokens + state.CacheReadInputTokens + state.CacheCreationInputTokens,
            state.CacheReadInputTokens,
            state.OutputTokens,
            state.AssistantText.ToString(),
            state.ToolCalls()) with
        {
            ProviderContent = new(
                providerId,
                JsonSerializer.Serialize(state.ContentBlocks(), WireJsonContext.Default.AnthropicContentBlocks)),
        };
    }

    // Stored content that no longer parses is rebuilt rather than failing every later request.
    private static List<ContentBlock> ReplayedBlocks(LLMMessage message)
    {
        try
        {
            return JsonSerializer.Deserialize(message.ProviderContent.Content, WireJsonContext.Default.AnthropicContentBlocks)
                ?? Blocks(message);
        }
        catch (JsonException)
        {
            return Blocks(message);
        }
    }

    private static List<ContentBlock> Blocks(LLMMessage message)
    {
        if (message.Role == LLMRole.Tool)
        {
            var result = ContentBlocks(message);
            return
            [
                new ContentBlock
                {
                    Type = "tool_result",
                    ToolUseId = message.ToolCallId,
                    Content = result.Count > 0 ? result : null,
                },
            ];
        }

        if (message.Role == LLMRole.System)
        {
            return [ContentBlock.TextBlock(
                $"<system-update>\n{System.Security.SecurityElement.Escape(message.Content)}\n</system-update>")];
        }

        return
        [
            .. ContentBlocks(message),
            .. message.ToolCalls.Select(call => new ContentBlock
            {
                Type = "tool_use",
                Id = call.Id,
                Name = call.Name,
                Input = ToolInput(call.ArgumentsJson),
            }),
        ];
    }

    // The API rejects empty text blocks.
    private static List<ContentBlock> ContentBlocks(LLMMessage message) =>
    [
        .. message.Contents
            .Where(part => part.Kind == LLMContentKind.Image || part.Text.Length > 0)
            .Select(part => part.Kind == LLMContentKind.Image
                ? new ContentBlock { Type = "image", Source = Source(part) }
                : ContentBlock.TextBlock(part.Text)),
    ];

    private static IEnumerable<ContentBlock> TextBlocks(string text) =>
        text.Length > 0 ? [ContentBlock.TextBlock(text)] : [];

    private static ImageSource Source(LLMContent part) =>
        part.ImageUrl.Length > 0
            ? new ImageSource { Type = "url", Url = part.ImageUrl }
            : new ImageSource
            {
                Type = "base64",
                MediaType = part.MediaType,
                Data = Convert.ToBase64String(part.ReadImage()),
            };

    // The API accepts only an object here; a malformed call the model once made
    // must not make every later request in the conversation fail.
    private static JsonElement ToolInput(string argumentsJson)
    {
        try
        {
            var input = JsonElement.Parse(argumentsJson);
            return input.ValueKind == JsonValueKind.Object ? input : EmptyObject;
        }
        catch (JsonException)
        {
            return EmptyObject;
        }
    }

    private static IEnumerable<LLMEvent> Consume(string data, ParseState state)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        switch (JsonRead.String(root, "type"))
        {
            case "message_start":
                if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
                {
                    state.MessageId = JsonRead.String(message, "id");
                    state.ReadUsage(message);
                }

                break;

            case "content_block_start":
                if (root.TryGetProperty("content_block", out var block) && block.ValueKind == JsonValueKind.Object)
                {
                    foreach (var published in StartBlock(JsonRead.Int(root, "index"), block, state))
                    {
                        yield return published;
                    }
                }

                break;

            case "content_block_delta":
                if (root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    foreach (var published in ContinueBlock(JsonRead.Int(root, "index"), delta, state))
                    {
                        yield return published;
                    }
                }

                break;

            case "content_block_stop":
                var index = JsonRead.Int(root, "index");
                if (state.Blocks.TryGetValue(index, out var stopped) && stopped.Type == "thinking")
                {
                    yield return LLMEvent.ReasoningDelta(
                        string.Empty, LLMReasoningKind.Summary, state.PartId(index), completed: true);
                }

                break;

            case "message_delta":
                if (root.TryGetProperty("delta", out var messageDelta) && messageDelta.ValueKind == JsonValueKind.Object
                    && JsonRead.TryReadString(messageDelta, "stop_reason", out var stopReason))
                {
                    state.FinishReason = MapStopReason(stopReason);
                }

                state.ReadUsage(root);
                break;

            case "message_stop":
                state.Stopped = true;
                break;

            // A structured error inside a 200 stream is terminal; raising it lets the
            // retry layer classify it exactly as it classifies an HTTP failure.
            case "error":
                var error = root.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object
                    ? nested
                    : root;
                throw new ProviderResponseException(
                    JsonRead.String(error, "type"),
                    string.Empty,
                    JsonRead.String(error, "message"),
                    ProviderErrors.BoundResponseBody(data));
        }
    }

    private static IEnumerable<LLMEvent> StartBlock(int index, JsonElement block, ParseState state)
    {
        var type = JsonRead.String(block, "type");
        switch (type)
        {
            case "text":
                var text = state.Blocks[index] = new BlockAccumulator { Type = type };
                foreach (var published in AppendText(text, JsonRead.String(block, "text"), state))
                {
                    yield return published;
                }

                break;

            case "thinking":
                var thinking = state.Blocks[index] = new BlockAccumulator { Type = type };
                _ = thinking.Signature.Append(JsonRead.String(block, "signature"));
                foreach (var published in AppendThinking(thinking, index, JsonRead.String(block, "thinking"), state))
                {
                    yield return published;
                }

                break;

            case "redacted_thinking":
                state.Blocks[index] = new BlockAccumulator { Type = type, Data = JsonRead.String(block, "data") };
                break;

            case "tool_use":
                state.Blocks[index] = new BlockAccumulator
                {
                    Type = type,
                    Id = JsonRead.String(block, "id"),
                    Name = JsonRead.String(block, "name"),
                    InitialInput = block.TryGetProperty("input", out var input) ? input.GetRawText() : "{}",
                };
                break;
        }
    }

    private static IEnumerable<LLMEvent> ContinueBlock(int index, JsonElement delta, ParseState state)
    {
        if (!state.Blocks.TryGetValue(index, out var block))
        {
            yield break;
        }

        switch (JsonRead.String(delta, "type"))
        {
            case "text_delta":
                foreach (var published in AppendText(block, JsonRead.String(delta, "text"), state))
                {
                    yield return published;
                }

                break;

            case "thinking_delta":
                foreach (var published in AppendThinking(block, index, JsonRead.String(delta, "thinking"), state))
                {
                    yield return published;
                }

                break;

            case "signature_delta":
                _ = block.Signature.Append(JsonRead.String(delta, "signature"));
                break;

            case "input_json_delta":
                _ = block.Text.Append(JsonRead.String(delta, "partial_json"));
                break;
        }
    }

    private static IEnumerable<LLMEvent> AppendText(BlockAccumulator block, string text, ParseState state)
    {
        if (text.Length == 0)
        {
            yield break;
        }

        _ = block.Text.Append(text);
        _ = state.AssistantText.Append(text);
        yield return LLMEvent.TextDelta(text);
    }

    private static IEnumerable<LLMEvent> AppendThinking(BlockAccumulator block, int index, string thinking, ParseState state)
    {
        if (thinking.Length == 0)
        {
            yield break;
        }

        _ = block.Text.Append(thinking);
        yield return LLMEvent.ReasoningDelta(thinking, LLMReasoningKind.Summary, state.PartId(index), completed: false);
    }

    private static string MapStopReason(string reason) =>
        reason switch
        {
            "end_turn" or "stop_sequence" => "stop",
            "tool_use" => "tool_calls",
            "max_tokens" or "model_context_window_exceeded" => "length",
            "refusal" => "content_filter",
            _ => "incomplete",
        };

    internal sealed class Body
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("max_tokens")]
        public required int MaxTokens { get; init; }

        [JsonPropertyName("system")]
        public IReadOnlyList<ContentBlock>? System { get; init; }

        [JsonPropertyName("messages")]
        public required IReadOnlyList<MessageParam> Messages { get; init; }

        [JsonPropertyName("tools")]
        public IReadOnlyList<ToolParam>? Tools { get; init; }

        [JsonPropertyName("stream")]
        public bool Stream { get; init; } = true;

        [JsonPropertyName("thinking")]
        public Thinking? Thinking { get; init; }

        [JsonPropertyName("output_config")]
        public OutputConfig? OutputConfig { get; init; }

        // Automatic caching: the breakpoint follows the growing conversation tail.
        [JsonPropertyName("cache_control")]
        public CacheControl CacheControl { get; init; } = Ephemeral;
    }

    internal sealed class MessageParam
    {
        [JsonPropertyName("role")]
        public required string Role { get; init; }

        [JsonPropertyName("content")]
        public required IReadOnlyList<ContentBlock> Content { get; init; }
    }

    internal sealed record ContentBlock
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("text")]
        public string? Text { get; init; }

        [JsonPropertyName("source")]
        public ImageSource? Source { get; init; }

        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("input")]
        public JsonElement? Input { get; init; }

        [JsonPropertyName("tool_use_id")]
        public string? ToolUseId { get; init; }

        [JsonPropertyName("content")]
        public IReadOnlyList<ContentBlock>? Content { get; init; }

        [JsonPropertyName("thinking")]
        public string? Thinking { get; init; }

        [JsonPropertyName("signature")]
        public string? Signature { get; init; }

        [JsonPropertyName("data")]
        public string? Data { get; init; }

        [JsonPropertyName("cache_control")]
        public CacheControl? CacheControl { get; init; }

        public static ContentBlock TextBlock(string text) => new() { Type = "text", Text = text };

        public ContentBlock Cached() => this with { CacheControl = Ephemeral };
    }

    internal sealed class ImageSource
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("media_type")]
        public string? MediaType { get; init; }

        [JsonPropertyName("data")]
        public string? Data { get; init; }

        [JsonPropertyName("url")]
        public string? Url { get; init; }
    }

    internal sealed class ToolParam
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("input_schema")]
        public JsonElement InputSchema { get; init; }
    }

    internal sealed class Thinking
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "adaptive";

        [JsonPropertyName("display")]
        public string Display { get; init; } = "summarized";

        // Replayed blocks become invalid whenever history before them is edited,
        // as compaction does; dropping them beats rejecting the request.
        [JsonPropertyName("block_binding")]
        public BlockBinding BlockBinding { get; init; } = new();
    }

    internal sealed class BlockBinding
    {
        [JsonPropertyName("prefix_mismatch_behavior")]
        public string PrefixMismatchBehavior { get; init; } = "drop_block";
    }

    internal sealed class OutputConfig
    {
        [JsonPropertyName("effort")]
        public required string Effort { get; init; }
    }

    internal sealed class CacheControl
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "ephemeral";
    }

    // One streamed content block; blocks of types this adapter does not request are not tracked.
    internal sealed class BlockAccumulator
    {
        public required string Type { get; init; }

        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        // tool_use: sent whole on content_block_start when no input_json_delta follows.
        public string InitialInput { get; init; } = "{}";

        public string Data { get; init; } = string.Empty;

        // The text, the thinking, or the tool input's partial JSON.
        public StringBuilder Text { get; } = new();

        public StringBuilder Signature { get; } = new();

        public string Arguments => Text.Length > 0 ? Text.ToString() : InitialInput;

        public IEnumerable<ContentBlock> ToContentBlocks() =>
            Type switch
            {
                "text" when Text.Length > 0 => [ContentBlock.TextBlock(Text.ToString())],
                "thinking" => [new ContentBlock { Type = Type, Thinking = Text.ToString(), Signature = Signature.ToString() }],
                "redacted_thinking" => [new ContentBlock { Type = Type, Data = Data }],
                "tool_use" => [new ContentBlock { Type = Type, Id = Id, Name = Name, Input = ToolInput(Arguments) }],
                _ => [],
            };
    }

    internal sealed class ParseState
    {
        public string MessageId { get; set; } = string.Empty;

        public bool Stopped { get; set; }

        public string FinishReason { get; set; } = "incomplete";

        public int InputTokens { get; private set; }

        public int CacheReadInputTokens { get; private set; }

        public int CacheCreationInputTokens { get; private set; }

        public int OutputTokens { get; private set; }

        public StringBuilder AssistantText { get; } = new();

        public SortedDictionary<int, BlockAccumulator> Blocks { get; } = [];

        public string PartId(int index) => $"{MessageId}:{index.ToString(CultureInfo.InvariantCulture)}";

        // message_start carries the input counts; message_delta carries cumulative counts.
        public void ReadUsage(JsonElement scope)
        {
            if (!scope.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            InputTokens = JsonRead.TryReadNonNegativeInt(usage, "input_tokens", out var input) ? input : InputTokens;
            CacheReadInputTokens = JsonRead.TryReadNonNegativeInt(usage, "cache_read_input_tokens", out var cacheRead)
                ? cacheRead
                : CacheReadInputTokens;
            CacheCreationInputTokens = JsonRead.TryReadNonNegativeInt(usage, "cache_creation_input_tokens", out var cacheCreation)
                ? cacheCreation
                : CacheCreationInputTokens;
            OutputTokens = JsonRead.TryReadNonNegativeInt(usage, "output_tokens", out var output) ? output : OutputTokens;
        }

        public IReadOnlyList<LLMToolCall> ToolCalls() =>
        [
            .. Blocks.Values
                .Where(block => block.Type == "tool_use")
                .Select(block => new LLMToolCall(block.Id, block.Name, block.Arguments)),
        ];

        public List<ContentBlock> ContentBlocks() => [.. Blocks.Values.SelectMany(block => block.ToContentBlocks())];
    }
}
