using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parrot.Llm.Wire;

// Adapts the OpenAI Responses API wire protocol to the neutral LLMEvent stream.
// Port of Go's protocol/responses. Reasoning-summary and reasoning-text deltas
// both fold onto ReasoningDelta; the terminal Completed event carries the
// assembled assistant text and tool calls.
internal static class ResponsesAdapter
{
    public static byte[] Encode(LLMRequest request)
    {
        var input = new List<InputItem>();

        foreach (var message in request.Messages)
        {
            // A tool result is its own input item, not a message.
            if (message.Role == LLMRole.Tool)
            {
                input.Add(new InputItem
                {
                    Type = "function_call_output",
                    CallId = message.ToolCallId,
                    Output = message.Content,
                });
                continue;
            }

            var role = message.Role == LLMRole.System ? "developer" : RoleName(message.Role);
            var content = new List<ContentPart>();

            if (message.Content.Length > 0)
            {
                var partType = message.Role == LLMRole.Assistant ? "output_text" : "input_text";
                content.Add(new ContentPart { Type = partType, Text = message.Content });
            }

            if (content.Count > 0 || message.ToolCalls.Count == 0)
            {
                input.Add(new InputItem { Type = "message", Role = role, Content = content });
            }

            foreach (var call in message.ToolCalls)
            {
                input.Add(new InputItem
                {
                    Type = "function_call",
                    CallId = call.Id,
                    Name = call.Name,
                    Arguments = call.ArgumentsJson,
                });
            }
        }

        var tools = request.Tools.Count == 0
            ? null
            : request.Tools
                .Select(tool => new FunctionTool
                {
                    Name = tool.Name,
                    Description = tool.Description.Length > 0 ? tool.Description : null,
                    Parameters = ParseSchema(tool.ParametersJson),
                })
                .ToList();

        Reasoning? reasoning = null;

        if (request.Reasoning is { } options && (options.Effort.Length > 0 || options.Summary.Length > 0))
        {
            reasoning = new Reasoning
            {
                Effort = options.Effort.Length > 0 ? options.Effort : null,
                Summary = options.Summary.Length > 0 ? options.Summary : null,
            };
        }

        var body = new Body
        {
            Model = request.Model,
            Instructions = request.Instructions.Length > 0 ? request.Instructions : null,
            Input = input,
            Tools = tools,
            Stream = true,
            Store = false,
            Reasoning = reasoning,
            Provider = WirePreferences.Normalize(request.ProviderPreferences),
        };

        return JsonSerializer.SerializeToUtf8Bytes(body, WireJsonContext.Default.ResponsesBody);
    }

    public static async IAsyncEnumerable<LLMEvent> Parse(
        Stream stream,
        int maxEventBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var state = new ParseState();

        await foreach (var record in SseDecoder.Decode(stream, maxEventBytes, cancellationToken).ConfigureAwait(false))
        {
            if (record.Data == "[DONE]")
            {
                throw new WireProtocolException("responses: provider stream completed without a terminal event");
            }

            foreach (var published in Consume(record.Data, state))
            {
                yield return published;
            }

            if (state.Done)
            {
                foreach (var toolCall in state.ToolCallEvents())
                {
                    yield return toolCall;
                }

                yield return state.Complete();
                yield break;
            }
        }

        throw new WireProtocolException("responses: unexpected provider EOF (stream ended without a terminal event)");
    }

    private static IEnumerable<LLMEvent> Consume(string data, ParseState state)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        var type = ReadString(root, "type");

        switch (type)
        {
            case "response.output_text.delta":
            {
                var delta = ReadString(root, "delta");

                if (delta.Length > 0)
                {
                    _ = state.AssistantText.Append(delta);
                    yield return LLMEvent.TextDelta(delta);
                }

                break;
            }

            case "response.reasoning_summary_text.delta":
            case "response.reasoning_text.delta":
            case "response.output_text.annotation.added":
            {
                var delta = ReadString(root, "delta");

                if (delta.Length > 0)
                {
                    yield return LLMEvent.ReasoningDelta(delta);
                }

                break;
            }

            case "response.output_item.added":
            {
                var item = Item(root);

                if (item.Type == "function_call")
                {
                    _ = state.AddTool(item.Id, item.CallId, item.Name, item.Arguments);
                }

                break;
            }

            case "response.function_call_arguments.delta":
            {
                var itemId = ReadString(root, "item_id");
                var callId = ReadString(root, "call_id");
                var accumulator = state.FindTool(itemId, callId)
                    ?? state.AddTool(itemId, callId, ReadString(root, "name"), string.Empty);
                var delta = ReadString(root, "delta");
                accumulator.Arguments += delta;
                break;
            }

            case "response.function_call_arguments.done":
            case "response.output_item.done":
            {
                var isItemDone = type == "response.output_item.done";
                var item = isItemDone ? Item(root) : default;
                var itemId = isItemDone ? item.Id : ReadString(root, "item_id");
                var callId = isItemDone ? item.CallId : ReadString(root, "call_id");
                var name = isItemDone ? item.Name : ReadString(root, "name");
                var arguments = isItemDone ? item.Arguments : ReadString(root, "arguments");

                if (isItemDone && item.Type != "function_call")
                {
                    break;
                }

                var accumulator = state.FindTool(itemId, callId);

                if (accumulator is null)
                {
                    _ = state.AddTool(itemId, callId, name, arguments);
                }
                else if (arguments.Length > 0)
                {
                    accumulator.Arguments = arguments;
                }

                break;
            }

            case "response.completed":
            case "response.incomplete":
            {
                ReadUsage(root, state);
                state.FinishReason = type == "response.completed"
                    ? (state.HasTools ? "tool_calls" : "stop")
                    : IncompleteReason(root);
                state.Done = true;
                break;
            }

            case "response.failed":
            case "error":
            {
                var failure = type == "response.failed" && root.TryGetProperty("response", out var response)
                    && response.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object
                    ? nested
                    : root.TryGetProperty("error", out var direct) && direct.ValueKind == JsonValueKind.Object
                        ? direct
                        : root;

                throw new ProviderResponseException(
                    ReadString(failure, "type"), ReadString(failure, "code"), ReadString(failure, "message"));
            }

            default:
                break;
        }
    }

    private static void ReadUsage(JsonElement root, ParseState state)
    {
        if (!root.TryGetProperty("response", out var response)
            || !response.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        state.InputTokens = ReadInt(usage, "input_tokens");
        state.CachedInputTokens = ReadCachedInputTokens(usage);
        state.OutputTokens = ReadInt(usage, "output_tokens");
    }

    private static ItemFields Item(JsonElement root)
    {
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        return new ItemFields(
            ReadString(item, "id"),
            ReadString(item, "type"),
            ReadString(item, "call_id"),
            ReadString(item, "name"),
            ReadString(item, "arguments"));
    }

    private static string IncompleteReason(JsonElement root)
    {
        if (root.TryGetProperty("response", out var response)
            && response.TryGetProperty("incomplete_details", out var details))
        {
            return ReadString(details, "reason") switch
            {
                "max_output_tokens" or "max_tokens" => "length",
                "content_filter" => "content_filter",
                _ => "incomplete",
            };
        }

        return "incomplete";
    }

    private static JsonElement ParseSchema(string schema)
    {
        using var document = JsonDocument.Parse(schema.Length > 0 ? schema : "{}");
        return document.RootElement.Clone();
    }

    private static string RoleName(LLMRole role) =>
        role == LLMRole.Assistant ? "assistant" : "user";

    private static string ReadString(JsonElement scope, string name) =>
        scope.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement scope, string name) =>
        scope.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    private static int ReadCachedInputTokens(JsonElement usage) =>
        usage.TryGetProperty("input_tokens_details", out var details)
            && details.ValueKind == JsonValueKind.Object
            ? ReadInt(details, "cached_tokens")
            : 0;

    private readonly record struct ItemFields(string Id, string Type, string CallId, string Name, string Arguments);

    internal sealed class Body
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("instructions")]
        public string? Instructions { get; init; }

        [JsonPropertyName("input")]
        public required IReadOnlyList<InputItem> Input { get; init; }

        [JsonPropertyName("tools")]
        public IReadOnlyList<FunctionTool>? Tools { get; init; }

        [JsonPropertyName("stream")]
        public bool Stream { get; init; }

        [JsonPropertyName("store")]
        public bool Store { get; init; }

        [JsonPropertyName("reasoning")]
        public Reasoning? Reasoning { get; init; }

        [JsonPropertyName("provider")]
        public JsonElement? Provider { get; init; }
    }

    // One input entry: a message, a function_call, or a function_call_output.
    internal sealed class InputItem
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("role")]
        public string? Role { get; init; }

        [JsonPropertyName("content")]
        public IReadOnlyList<ContentPart>? Content { get; init; }

        [JsonPropertyName("call_id")]
        public string? CallId { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("arguments")]
        public string? Arguments { get; init; }

        [JsonPropertyName("output")]
        public string? Output { get; init; }
    }

    internal sealed class ContentPart
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        [JsonPropertyName("text")]
        public required string Text { get; init; }
    }

    internal sealed class FunctionTool
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "function";

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("parameters")]
        public JsonElement Parameters { get; init; }

        [JsonPropertyName("strict")]
        public bool Strict { get; init; }
    }

    internal sealed class Reasoning
    {
        [JsonPropertyName("effort")]
        public string? Effort { get; init; }

        [JsonPropertyName("summary")]
        public string? Summary { get; init; }
    }

    internal sealed class ToolAccumulator
    {
        public string Key { get; set; } = string.Empty;

        public string ItemId { get; set; } = string.Empty;

        public string CallId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Arguments { get; set; } = string.Empty;

        public string ToolId() => CallId.Length > 0 ? CallId : ItemId;
    }

    internal sealed class ParseState
    {
        private readonly Dictionary<string, ToolAccumulator> _tools = [];
        private readonly Dictionary<string, string> _aliases = [];

        public bool Done { get; set; }

        public bool HasTools => _tools.Count > 0;

        public string FinishReason { get; set; } = string.Empty;

        public int InputTokens { get; set; }

        public int CachedInputTokens { get; set; }

        public int OutputTokens { get; set; }

        public StringBuilder AssistantText { get; } = new();

        public ToolAccumulator AddTool(string itemId, string callId, string name, string arguments)
        {
            var key = itemId.Length > 0 ? itemId : callId;
            var item = FindTool(itemId, callId);

            if (item is null)
            {
                item = new ToolAccumulator
                {
                    Key = key,
                    ItemId = itemId,
                    CallId = callId,
                    Name = name,
                    Arguments = arguments,
                };
                _tools[key] = item;
            }
            else
            {
                if (item.ItemId.Length == 0)
                {
                    item.ItemId = itemId;
                }

                if (item.CallId.Length == 0)
                {
                    item.CallId = callId;
                }

                if (item.Name.Length == 0)
                {
                    item.Name = name;
                }

                if (item.Arguments.Length == 0)
                {
                    item.Arguments = arguments;
                }

                key = item.Key;
            }

            if (itemId.Length > 0)
            {
                _aliases[itemId] = key;
            }

            if (callId.Length > 0)
            {
                _aliases[callId] = key;
            }

            return item;
        }

        public ToolAccumulator? FindTool(string itemId, string callId)
        {
            foreach (var candidate in new[] { itemId, callId })
            {
                if (candidate.Length == 0)
                {
                    continue;
                }

                if (_aliases.TryGetValue(candidate, out var key) && _tools.TryGetValue(key, out var aliased))
                {
                    return aliased;
                }

                if (_tools.TryGetValue(candidate, out var direct))
                {
                    return direct;
                }
            }

            return null;
        }

        public IEnumerable<LLMEvent> ToolCallEvents() =>
            _tools.Keys.OrderBy(key => key, StringComparer.Ordinal)
                .Select(key => _tools[key])
                .Select(call => LLMEvent.ToolCallDelta(call.ToolId(), call.Name, call.Arguments));

        public LLMEvent Complete() =>
            LLMEvent.Completed(
                FinishReason,
                InputTokens,
                CachedInputTokens,
                OutputTokens,
                AssistantText.ToString(),
                [
                    .. _tools.Keys.OrderBy(key => key, StringComparer.Ordinal)
                        .Select(key => _tools[key])
                        .Select(call => new LLMToolCall(call.ToolId(), call.Name, call.Arguments)),
                ]);
    }
}
