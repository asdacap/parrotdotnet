using System.Text.Json;
using Parrot.Llm.Wire;

namespace Parrot.Llm;

// Backs the kimi-api provider: the Moonshot platform API, billed against a
// prepaid balance. Streams over the same compatible transport and additionally
// reports that balance. Composes the base provider rather than inheriting it.
internal sealed class KimiProvider : ILLMProvider, IUsageReporter
{
    private readonly OpenAICompatibleProvider _inner;
    private readonly HttpClient _client;
    private readonly Uri _balanceEndpoint;
    private readonly string _apiKey;

    public KimiProvider(OpenAICompatibleOptions options, HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(options);
        _inner = new OpenAICompatibleProvider(options, client);
        _client = client;
        _balanceEndpoint = HttpStreaming.EndpointUrl(options.BaseUrl, "users/me/balance", options.AllowInsecureLocalhost);
        _apiKey = options.ApiKey;
    }

    public string Id => _inner.Id;

    public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
        _inner.ListModels(cancellationToken);

    public IReadOnlyList<LLMModel> SeedModels() => _inner.SeedModels();

    public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken) =>
        _inner.Call(request, cancellationToken);

    public async Task<SubscriptionUsage> Usage(CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Authorization"] = "Bearer " + _apiKey,
        };

        var body = await HttpStreaming
            .Get(_client, _balanceEndpoint, headers, HttpStreaming.RequestTimeout, HttpStreaming.MaxErrorBytes, cancellationToken)
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
