using System.Text.Json;
using Parrot.Llm.Wire;

namespace Parrot.Llm;

internal sealed class KimiUsageReporter(OpenAICompatibleOptions options, HttpClient client) : IUsageReporter
{
    private readonly Uri _balanceEndpoint = HttpStreaming.EndpointUrl(
        options.BaseUrl, "users/me/balance", options.AllowInsecureLocalhost, options.AllowInsecureRemote);

    private readonly IApiKeySource _apiKeySource = options.ApiKeySource;
    private readonly string _providerId = options.Id;

    public async Task<SubscriptionUsage> Usage(CancellationToken cancellationToken)
    {
        var body = await HttpStreaming
            .Get(
                client,
                _balanceEndpoint,
                await HttpStreaming.ResolveBearerHeaders(_apiKeySource, _providerId, cancellationToken).ConfigureAwait(false),
                HttpStreaming.RequestTimeout,
                HttpStreaming.MaxErrorBytes,
                cancellationToken)
            .ConfigureAwait(false);

        using var document = JsonDocument.Parse(body);

        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new LLMProviderException("provider: usage response has no balance");
        }

        var available = JsonRead.Number(data, "available_balance");
        var balance = data.TryGetProperty("available_balance", out var raw) ? raw.GetRawText().Trim('"') : "0";

        return new SubscriptionUsage { Credits = new UsageCredits(available > 0, balance) };
    }
}
