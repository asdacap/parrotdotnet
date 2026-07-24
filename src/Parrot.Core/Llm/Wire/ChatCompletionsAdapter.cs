using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parrot.Llm.Wire;

// Adapts the OpenAI-compatible Chat Completions wire protocol to the neutral
// LLMEvent stream. Port of Go's protocol/chatcompletions. The terminal
// Completed event carries the assembled assistant text and tool calls, so a
// session never reassembles fragments itself.
internal static class ChatCompletionsAdapter
{
    public static byte[] Encode(LLMRequest request)
    {
        var messages = new List<Message>();

        if (request.Instructions.Length > 0)
        {
            messages.Add(new Message { Role = "system", Content = request.Instructions });
        }

        foreach (var message in request.Messages)
        {
            messages.Add(new Message
            {
                Role = RoleName(message.Role),
                Content = message.Content.Length == 0 && message.ToolCalls.Count > 0 ? null : message.Content,
                ToolCallId = message.ToolCallId.Length == 0 ? null : message.ToolCallId,
                ToolCalls = message.ToolCalls.Count == 0
                    ? null
                    : [.. message.ToolCalls.Select(call => new ToolCall
                    {
                        Id = call.Id,
                        Function = new CallFunction { Name = call.Name, Arguments = call.ArgumentsJson },
                    })],
            });
        }

        var tools = request.Tools.Count == 0
            ? null
            : request.Tools
                .Select(tool => new Tool
                {
                    Function = new Function
                    {
                        Name = tool.Name,
                        Description = tool.Description.Length > 0 ? tool.Description : null,
                        Parameters = ParseSchema(tool.ParametersJson),
                    },
                })
                .ToList();

        var body = new Body
        {
            Model = request.Model,
            Messages = messages,
            Tools = tools,
            Stream = true,
            StreamOptions = new StreamOptions { IncludeUsage = true },
            ReasoningEffort = request.Reasoning?.Effort is { Length: > 0 } effort ? effort : null,
            Provider = WirePreferences.Normalize(request.ProviderPreferences),
            IncludeRouterMetadata = request.IncludeRouterMetadata ? true : null,
            MaxTokens = request.MaxTokens > 0 ? request.MaxTokens : null,
        };

        return JsonSerializer.SerializeToUtf8Bytes(body, WireJsonContext.Default.ChatCompletionsBody);
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
                break;
            }

            foreach (var published in Consume(record.Data, state))
            {
                yield return published;
            }
        }

        if (!state.FinishSeen)
        {
            throw new WireProtocolException(
                "chatcompletions: unexpected provider EOF (stream ended without a terminal event)");
        }

        yield return state.Complete();
    }

    private static IEnumerable<LLMEvent> Consume(string data, ParseState state)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        // A structured error inside a 200 stream is terminal; raising it lets the
        // retry layer classify it exactly as it classifies an HTTP failure.
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            throw new ProviderResponseException(
                ReadString(error, "type"), ReadScalar(error, "code"), ReadString(error, "message"));
        }

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                foreach (var published in ConsumeChoice(choice, state))
                {
                    yield return published;
                }
            }
        }

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            state.InputTokens = ReadInt(usage, "prompt_tokens");
            state.OutputTokens = ReadInt(usage, "completion_tokens");
        }
    }

    private static IEnumerable<LLMEvent> ConsumeChoice(JsonElement choice, ParseState state)
    {
        if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
        {
            var content = ReadString(delta, "content");

            if (content.Length > 0)
            {
                _ = state.AssistantText.Append(content);
                yield return LLMEvent.TextDelta(content);
            }

            var reasoning = ReadString(delta, "reasoning_content");

            if (reasoning.Length == 0)
            {
                reasoning = ReadString(delta, "reasoning");
            }

            if (reasoning.Length > 0)
            {
                yield return LLMEvent.ReasoningDelta(reasoning);
            }

            if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var published in ConsumeToolCalls(toolCalls, state))
                {
                    yield return published;
                }
            }
        }

        if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
        {
            state.FinishSeen = true;
            state.FinishReason = MapFinishReason(finish.GetString() ?? string.Empty);
        }
    }

    private static IEnumerable<LLMEvent> ConsumeToolCalls(JsonElement toolCalls, ParseState state)
    {
        foreach (var toolCall in toolCalls.EnumerateArray())
        {
            var accumulator = state.Tool(ReadInt(toolCall, "index"));
            var id = ReadString(toolCall, "id");

            if (id.Length > 0)
            {
                accumulator.Id = id;
            }

            var arguments = string.Empty;

            if (toolCall.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
            {
                var name = ReadString(function, "name");

                if (name.Length > 0)
                {
                    accumulator.Name = name;
                }

                arguments = ReadString(function, "arguments");
            }

            _ = accumulator.Arguments.Append(arguments);

            if (arguments.Length > 0)
            {
                yield return LLMEvent.ToolCallDelta(accumulator.Id, accumulator.Name, arguments);
            }
        }
    }

    private static JsonElement ParseSchema(string schema)
    {
        using var document = JsonDocument.Parse(schema.Length > 0 ? schema : "{}");
        return document.RootElement.Clone();
    }

    private static string RoleName(LLMRole role) =>
        role switch
        {
            LLMRole.System => "system",
            LLMRole.Assistant => "assistant",
            LLMRole.Tool => "tool",
            _ => "user",
        };

    private static string MapFinishReason(string reason) =>
        reason switch
        {
            "stop" => "stop",
            "tool_calls" or "function_call" => "tool_calls",
            "length" or "max_tokens" => "length",
            "content_filter" => "content_filter",
            _ => "incomplete",
        };

    private static string ReadString(JsonElement scope, string name) =>
        scope.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string ReadScalar(JsonElement scope, string name)
    {
        if (!scope.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty,
        };
    }

    private static int ReadInt(JsonElement scope, string name) =>
        scope.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    internal sealed class Body
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("messages")]
        public required IReadOnlyList<Message> Messages { get; init; }

        [JsonPropertyName("tools")]
        public IReadOnlyList<Tool>? Tools { get; init; }

        [JsonPropertyName("stream")]
        public bool Stream { get; init; }

        [JsonPropertyName("stream_options")]
        public required StreamOptions StreamOptions { get; init; }

        [JsonPropertyName("reasoning_effort")]
        public string? ReasoningEffort { get; init; }

        [JsonPropertyName("provider")]
        public JsonElement? Provider { get; init; }

        [JsonPropertyName("include_router_metadata")]
        public bool? IncludeRouterMetadata { get; init; }

        [JsonPropertyName("max_tokens")]
        public int? MaxTokens { get; init; }
    }

    internal sealed class Message
    {
        [JsonPropertyName("role")]
        public required string Role { get; init; }

        [JsonPropertyName("content")]
        public string? Content { get; init; }

        [JsonPropertyName("tool_call_id")]
        public string? ToolCallId { get; init; }

        [JsonPropertyName("tool_calls")]
        public IReadOnlyList<ToolCall>? ToolCalls { get; init; }
    }

    internal sealed class ToolCall
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("type")]
        public string Type { get; init; } = "function";

        [JsonPropertyName("function")]
        public required CallFunction Function { get; init; }
    }

    internal sealed class CallFunction
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("arguments")]
        public required string Arguments { get; init; }
    }

    internal sealed class Tool
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "function";

        [JsonPropertyName("function")]
        public required Function Function { get; init; }
    }

    internal sealed class Function
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("parameters")]
        public JsonElement Parameters { get; init; }
    }

    internal sealed class StreamOptions
    {
        [JsonPropertyName("include_usage")]
        public bool IncludeUsage { get; init; }
    }

    internal sealed class ToolAccumulator
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public StringBuilder Arguments { get; } = new();
    }

    internal sealed class ParseState
    {
        private readonly SortedDictionary<int, ToolAccumulator> _tools = [];

        public bool FinishSeen { get; set; }

        public string FinishReason { get; set; } = string.Empty;

        public int InputTokens { get; set; }

        public int OutputTokens { get; set; }

        public StringBuilder AssistantText { get; } = new();

        public ToolAccumulator Tool(int index)
        {
            if (!_tools.TryGetValue(index, out var accumulator))
            {
                accumulator = new ToolAccumulator();
                _tools[index] = accumulator;
            }

            return accumulator;
        }

        public LLMEvent Complete() =>
            LLMEvent.Completed(
                FinishReason,
                InputTokens,
                OutputTokens,
                AssistantText.ToString(),
                [.. _tools.Values.Select(call => new LLMToolCall(call.Id, call.Name, call.Arguments.ToString()))]);
    }
}
