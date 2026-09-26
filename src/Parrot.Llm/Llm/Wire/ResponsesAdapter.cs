using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parrot.Llm.Wire;

// Adapts the OpenAI Responses API wire protocol to the neutral LLMEvent stream.
// Port of Go's protocol/responses. The terminal Completed event carries the
// assembled assistant text and tool calls.
internal static class ResponsesAdapter
{
    public static byte[] Encode(LLMRequest request) => Prepare(request).EncodeHttp();

    public static PreparedRequest Prepare(LLMRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var input = new List<InputItem>();

        foreach (var message in request.Messages)
        {
            AddMessageInput(message, input);
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

        return new PreparedRequest(new Body
        {
            Model = request.Model,
            Instructions = request.Instructions.Length > 0 ? request.Instructions : null,
            Input = input,
            Tools = tools,
            Stream = true,
            Store = false,
            Reasoning = reasoning,
            Provider = WirePreferences.Normalize(request.ProviderPreferences),
            MaxOutputTokens = request.MaxOutputTokens > 0 ? request.MaxOutputTokens : null,
        });
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

            foreach (var published in state.Consume(record.Data))
            {
                yield return published;
            }

            if (state.Done)
            {
                foreach (var published in state.CompleteEvents())
                {
                    yield return published;
                }

                yield break;
            }
        }

        throw new WireProtocolException("responses: unexpected provider EOF (stream ended without a terminal event)");
    }

    private static void AddMessageInput(LLMMessage message, List<InputItem> input)
    {
        if (message.Role == LLMRole.Tool)
        {
            input.Add(new InputItem
            {
                Type = "function_call_output",
                CallId = message.ToolCallId,
                Output = message.Content,
            });
            return;
        }

        var role = message.Role == LLMRole.System ? "developer" : RoleName(message.Role);
        var content = new List<ContentPart>();

        foreach (var part in message.Contents)
        {
            if (part.Kind == LLMContentKind.Image)
            {
                content.Add(new ContentPart
                {
                    Type = "input_image",
                    ImageUrl = DataUrl(part),
                });
            }
            else if (part.Text.Length > 0)
            {
                var partType = message.Role == LLMRole.Assistant ? "output_text" : "input_text";
                content.Add(new ContentPart { Type = partType, Text = part.Text });
            }
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

    private static IEnumerable<LLMEvent> Consume(string data, ParseState state)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        var type = ReadString(root, "type");
        state.ValidateLifecycle(root, type);
        if (type == "response.metadata"
            && root.TryGetProperty("headers", out var headers)
            && headers.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in headers.EnumerateObject())
            {
                if (header.Name.Equals("x-codex-turn-state", StringComparison.OrdinalIgnoreCase)
                    && header.Value.ValueKind == JsonValueKind.String
                    && header.Value.GetString() is { Length: > 0 } turnState)
                {
                    state.CaptureTurnState(turnState);
                    break;
                }
            }
        }

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
            case "response.reasoning_summary_text.done":
            case "response.reasoning_text.done":
            {
                var delta = ReadString(root, "delta");
                var completed = type.EndsWith(".done", StringComparison.Ordinal);
                var reasoningKind = type is "response.reasoning_summary_text.delta"
                    or "response.reasoning_summary_text.done"
                    ? LLMReasoningKind.Summary
                    : LLMReasoningKind.Raw;

                if (delta.Length > 0 || completed)
                {
                    yield return LLMEvent.ReasoningDelta(
                        delta, reasoningKind, ReadReasoningPartId(root, type), completed);
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

                if (isItemDone)
                {
                    state.CaptureOutputItem(root);
                }

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
                    ReadString(failure, "type"),
                    ReadString(failure, "code"),
                    ReadString(failure, "message"),
                    ProviderErrors.BoundResponseBody(data));
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

    private static string DataUrl(LLMContent content) =>
        $"data:{content.MediaType};base64,{Convert.ToBase64String(content.ReadImage())}";

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

    private static string ReadReasoningPartId(JsonElement root, string type)
    {
        var itemId = ReadString(root, "item_id");
        var indexName = type switch
        {
            "response.reasoning_summary_text.delta" or "response.reasoning_summary_text.done" => "summary_index",
            "response.reasoning_text.delta" or "response.reasoning_text.done" => "content_index",
            "response.output_text.annotation.added" => "annotation_index",
            _ => string.Empty,
        };
        var index = indexName.Length > 0
            && root.TryGetProperty(indexName, out var value)
            && value.ValueKind == JsonValueKind.Number
                ? value.GetRawText()
                : string.Empty;

        if (itemId.Length == 0)
        {
            return index;
        }

        return index.Length > 0 ? $"{itemId}:{index}" : itemId;
    }

    private static int ReadCachedInputTokens(JsonElement usage) =>
        usage.TryGetProperty("input_tokens_details", out var details)
            && details.ValueKind == JsonValueKind.Object
            ? ReadInt(details, "cached_tokens")
            : 0;

    private readonly record struct ItemFields(string Id, string Type, string CallId, string Name, string Arguments);

    internal sealed record PreparedRequest(Body Body)
    {
        public IReadOnlyList<InputItem> Input => Body.Input;

        public byte[] EncodeHttp() => JsonSerializer.SerializeToUtf8Bytes(Body, WireJsonContext.Default.ResponsesBody);

        public byte[] EncodeWebSocket(
            string previousResponseId,
            IReadOnlyList<InputItem> input,
            string turnState) =>
            JsonSerializer.SerializeToUtf8Bytes(
                new WebSocketRequest
                {
                    Model = Body.Model,
                    Instructions = Body.Instructions,
                    PreviousResponseId = previousResponseId.Length > 0 ? previousResponseId : null,
                    Input = input,
                    Tools = Body.Tools,
                    Stream = Body.Stream,
                    Store = Body.Store,
                    Reasoning = Body.Reasoning,
                    Provider = Body.Provider,
                    MaxOutputTokens = Body.MaxOutputTokens,
                    ClientMetadata = turnState.Length > 0
                        ? new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["x-codex-turn-state"] = turnState,
                        }
                        : null,
                },
                WireJsonContext.Default.ResponsesWebSocketRequest);
    }

    internal sealed class WebSocketRequest
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "response.create";

        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("instructions")]
        public string? Instructions { get; init; }

        [JsonPropertyName("previous_response_id")]
        public string? PreviousResponseId { get; init; }

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

        [JsonPropertyName("max_output_tokens")]
        public int? MaxOutputTokens { get; init; }

        [JsonPropertyName("client_metadata")]
        public IReadOnlyDictionary<string, string>? ClientMetadata { get; init; }
    }

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

        [JsonPropertyName("max_output_tokens")]
        public int? MaxOutputTokens { get; init; }
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
        public string? Text { get; init; }

        [JsonPropertyName("image_url")]
        public string? ImageUrl { get; init; }
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
        private readonly List<ToolAccumulator> _toolOrder = [];
        private readonly List<InputItem> _output = [];
        private readonly List<InputItem> _streamedOutput = [];
        private string _createdResponseId = string.Empty;
        private long? _sequenceNumber;

        public Action<string> CaptureTurnState { get; init; } = static _ => { };

        public bool Done { get; set; }

        public string ResponseId { get; private set; } = string.Empty;

        public IReadOnlyList<InputItem> Output => _output;

        public bool HasTools => _tools.Count > 0;

        public string FinishReason { get; set; } = string.Empty;

        public int InputTokens { get; set; }

        public int CachedInputTokens { get; set; }

        public int OutputTokens { get; set; }

        public StringBuilder AssistantText { get; } = new();

        public IEnumerable<LLMEvent> Consume(string data)
        {
            if (Done)
            {
                throw new WireProtocolException("responses: event arrived after the terminal event");
            }

            foreach (var published in ResponsesAdapter.Consume(data, this))
            {
                yield return published;
            }

            if (Done)
            {
                CaptureCompletion(data);
            }
        }

        public IEnumerable<LLMEvent> CompleteEvents()
        {
            foreach (var toolCall in ToolCallEvents())
            {
                yield return toolCall;
            }

            yield return Complete();
        }

        public ToolAccumulator AddTool(string itemId, string callId, string name, string arguments)
        {
            var key = itemId.Length > 0 ? itemId : callId;
            var item = FindTool(itemId, callId);

            if (item is null)
            {
                if (key.Length == 0)
                {
                    key = "missing";
                    while (_tools.ContainsKey(key))
                    {
                        key += ":";
                    }
                }

                item = new ToolAccumulator
                {
                    Key = key,
                    ItemId = itemId,
                    CallId = callId,
                    Name = name,
                    Arguments = arguments,
                };
                _tools[key] = item;
                _toolOrder.Add(item);
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
            _toolOrder.Select(call => LLMEvent.ToolCallDelta(call.ToolId(), call.Name, call.Arguments));

        public LLMEvent Complete() =>
            LLMEvent.Completed(
                FinishReason,
                InputTokens,
                CachedInputTokens,
                OutputTokens,
                AssistantText.ToString(),
                [.. _toolOrder.Select(call => new LLMToolCall(call.ToolId(), call.Name, call.Arguments))]);

        public void ValidateLifecycle(JsonElement root, string type)
        {
            if (root.TryGetProperty("sequence_number", out var sequence)
                && sequence.ValueKind == JsonValueKind.Number)
            {
                if (!sequence.TryGetInt64(out var suppliedSequence))
                {
                    throw new WireProtocolException("responses: sequence_number must be an integer");
                }

                if (_sequenceNumber is { } previousSequence && suppliedSequence <= previousSequence)
                {
                    throw new WireProtocolException("responses: sequence_number must be strictly increasing");
                }

                _sequenceNumber = suppliedSequence;
            }

            if (type == "response.created")
            {
                _createdResponseId = ReadNestedResponseId(root);
                return;
            }

            if (type is not ("response.completed" or "response.incomplete" or "response.failed"))
            {
                return;
            }

            var terminalResponseId = ReadNestedResponseId(root);
            if (_createdResponseId.Length > 0
                && terminalResponseId.Length > 0
                && !terminalResponseId.Equals(_createdResponseId, StringComparison.Ordinal))
            {
                throw new WireProtocolException("responses: terminal response id does not match response.created");
            }
        }

        public void CaptureOutputItem(JsonElement root)
        {
            if (root.TryGetProperty("item", out var item)
                && item.ValueKind == JsonValueKind.Object
                && OutputItem(item) is { } captured)
            {
                _streamedOutput.Add(captured);
            }
        }

        private static string ReadNestedResponseId(JsonElement root) =>
            root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object
                ? ReadString(response, "id")
                : string.Empty;

        private static List<ContentPart> ReadContent(JsonElement item)
        {
            var content = new List<ContentPart>();
            if (item.TryGetProperty("content", out var parts) && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in parts.EnumerateArray())
                {
                    content.Add(new ContentPart
                    {
                        Type = ReadString(part, "type"),
                        Text = ReadString(part, "text"),
                    });
                }
            }

            return content;
        }

        private static InputItem? OutputItem(JsonElement item) =>
            ReadString(item, "type") switch
            {
                "function_call" => new InputItem
                {
                    Type = "function_call",
                    CallId = ReadString(item, "call_id"),
                    Name = ReadString(item, "name"),
                    Arguments = ReadString(item, "arguments"),
                },
                "message" => new InputItem
                {
                    Type = "message",
                    Role = ReadString(item, "role"),
                    Content = ReadContent(item),
                },
                _ => null,
            };

        private void CaptureCompletion(string data)
        {
            using var document = JsonDocument.Parse(data);
            if (!document.RootElement.TryGetProperty("response", out var response)
                || response.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            ResponseId = ReadString(response, "id");
            if (response.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                _output.AddRange(output.EnumerateArray().Select(OutputItem).OfType<InputItem>());
            }

            // The ChatGPT backend leaves the terminal output empty; its items arrive only as output_item.done.
            if (_output.Count == 0)
            {
                _output.AddRange(_streamedOutput);
            }
        }
    }
}
