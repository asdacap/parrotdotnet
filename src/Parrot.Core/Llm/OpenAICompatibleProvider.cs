using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Parrot.Llm;

// One provider for every endpoint speaking chat-completions. It holds no
// conversation and nothing between calls; the sink is a parameter, so there is
// no outbound dependency to hold either.
internal sealed class OpenAICompatibleProvider(string id, Uri baseAddress, string apiKey, HttpClient client)
    : ILLMProvider
{
    public string Id { get; } = id;

    public async Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(baseAddress, "models"));
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new LLMProviderException($"{Id} model list returned {(int)response.StatusCode}");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var listed = JsonSerializer.Deserialize(body, LlmJsonContext.Default.WireModelList);

        return
        [
            .. (listed?.Data ?? [])
                .Where(model => !string.IsNullOrEmpty(model.Id))
                .Select(model => new LLMModel(model.Id ?? string.Empty, Id)),
        ];
    }

    public async IAsyncEnumerable<LLMEvent> Call(
        LLMRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress, "chat/completions"))
        {
            Content = JsonContent.Create(ToWire(request), LlmJsonContext.Default.WireRequest),
        };

        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await client
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new LLMProviderException($"{Id} returned {(int)response.StatusCode}: {Truncate(body)}");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        await using (stream.ConfigureAwait(false))
        {
            await foreach (var published in Consume(stream, cancellationToken).ConfigureAwait(false))
            {
                yield return published;
            }
        }
    }

    // The last event is Completed, so a consumer that reads to the end has the
    // outcome without a second channel.
    internal static async IAsyncEnumerable<LLMEvent> Consume(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        var finishReason = string.Empty;
        var inputTokens = 0;
        var outputTokens = 0;
        var assistantText = new System.Text.StringBuilder();

        // Tool-call fragments arrive across many deltas, keyed by index: the
        // first carries id and name, the rest append argument text.
        var toolCalls = new SortedDictionary<int, ToolCallAssembly>();

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line[5..].Trim();

            if (payload.Length == 0 || string.Equals(payload, "[DONE]", StringComparison.Ordinal))
            {
                continue;
            }

            var chunk = JsonSerializer.Deserialize(payload, LlmJsonContext.Default.ChatCompletionsWire);
            var choice = chunk?.Choices is { Count: > 0 } choices ? choices[0] : null;

            if (chunk?.Usage is { } usage)
            {
                inputTokens = usage.PromptTokens;
                outputTokens = usage.CompletionTokens;
            }

            if (choice?.FinishReason is { Length: > 0 } reason)
            {
                finishReason = reason;
            }

            if (choice?.Delta?.Content is { Length: > 0 } content)
            {
                _ = assistantText.Append(content);
                yield return LLMEvent.TextDelta(content);
            }

            if (choice?.Delta?.ReasoningContent is { Length: > 0 } thought)
            {
                yield return LLMEvent.ReasoningDelta(thought);
            }

            foreach (var fragment in choice?.Delta?.ToolCalls ?? [])
            {
                var assembly = Accumulate(toolCalls, fragment);

                if (fragment.Function?.Arguments is { Length: > 0 } argumentFragment)
                {
                    yield return LLMEvent.ToolCallDelta(assembly.Id, assembly.Name, argumentFragment);
                }
            }
        }

        yield return LLMEvent.Completed(
            finishReason,
            inputTokens,
            outputTokens,
            assistantText.ToString(),
            [.. toolCalls.Values.Select(call => new LLMToolCall(call.Id, call.Name, call.Arguments.ToString()))]);
    }

    private static WireRequest ToWire(LLMRequest request) =>
        new()
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            Messages = [.. request.Messages.Select(ToWireMessage)],
            Tools = request.Tools.Count == 0 ? null : [.. request.Tools.Select(ToWireTool)],
        };

    private static WireMessage ToWireMessage(LLMMessage message) =>
        new()
        {
            Role = message.Role switch
            {
                LLMRole.System => "system",
                LLMRole.Assistant => "assistant",
                LLMRole.Tool => "tool",
                _ => "user",
            },
            Content = message.Content.Length == 0 && message.ToolCalls.Count > 0 ? null : message.Content,
            ToolCallId = message.ToolCallId.Length == 0 ? null : message.ToolCallId,
            ToolCalls = message.ToolCalls.Count == 0 ? null :
            [
                .. message.ToolCalls.Select(call => new WireToolCall
                {
                    Id = call.Id,
                    Type = "function",
                    Function = new WireToolCallFunction { Name = call.Name, Arguments = call.ArgumentsJson },
                }),
            ],
        };

    private static WireTool ToWireTool(LLMToolDefinition tool) =>
        new()
        {
            Function = new WireToolFunction
            {
                Name = tool.Name,
                Description = tool.Description,
                Parameters = System.Text.Json.Nodes.JsonNode.Parse(tool.ParametersJson),
            },
        };

    private static ToolCallAssembly Accumulate(
        SortedDictionary<int, ToolCallAssembly> calls, WireToolCall fragment)
    {
        var index = fragment.Index ?? 0;

        if (!calls.TryGetValue(index, out var assembly))
        {
            assembly = new ToolCallAssembly();
            calls[index] = assembly;
        }

        if (fragment.Id is { Length: > 0 } id)
        {
            assembly.Id = id;
        }

        if (fragment.Function?.Name is { Length: > 0 } name)
        {
            assembly.Name = name;
        }

        if (fragment.Function?.Arguments is { Length: > 0 } arguments)
        {
            _ = assembly.Arguments.Append(arguments);
        }

        return assembly;
    }

    private static string Truncate(string body) =>
        body.Length <= 300 ? body : body[..300];
}
