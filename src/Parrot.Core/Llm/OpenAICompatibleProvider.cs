using System.Net.Http.Json;
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

    public async Task<LLMResult> Call(
        LLMRequest request,
        ILLMEventSink events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(events);

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

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await Consume(stream, events, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<LLMResult> Consume(
        Stream stream,
        ILLMEventSink events,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        var text = new System.Text.StringBuilder();
        var reasoning = new System.Text.StringBuilder();
        var finishReason = string.Empty;
        var usage = LLMUsage.None;

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

            if (chunk?.Usage is { } wireUsage)
            {
                usage = new LLMUsage(wireUsage.PromptTokens, wireUsage.CompletionTokens);
            }

            if (choice?.FinishReason is { Length: > 0 } reason)
            {
                finishReason = reason;
            }

            if (choice?.Delta?.Content is { Length: > 0 } content)
            {
                _ = text.Append(content);
                await events.Publish(LLMEvent.TextDelta(content), cancellationToken).ConfigureAwait(false);
            }

            if (choice?.Delta?.ReasoningContent is { Length: > 0 } thought)
            {
                _ = reasoning.Append(thought);
                await events.Publish(LLMEvent.ReasoningDelta(thought), cancellationToken).ConfigureAwait(false);
            }
        }

        return new LLMResult
        {
            Text = text.ToString(),
            Reasoning = reasoning.ToString(),
            FinishReason = finishReason,
            Usage = usage,
        };
    }

    private static WireRequest ToWire(LLMRequest request) =>
        new()
        {
            Model = request.Model,
            MaxTokens = request.MaxTokens,
            Messages = [.. request.Messages.Select(message => new WireMessage
            {
                Role = message.Role switch
                {
                    LLMRole.System => "system",
                    LLMRole.Assistant => "assistant",
                    _ => "user",
                },
                Content = message.Content,
            })],
        };

    private static string Truncate(string body) =>
        body.Length <= 300 ? body : body[..300];
}
