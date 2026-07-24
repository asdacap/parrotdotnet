using System.Globalization;
using System.Text.Json;
using Parrot.Llm.Wire;

namespace Parrot.Llm;

// Backs the opencode-go provider: streams through the same compatible transport
// as any configured provider and additionally reports subscription usage from
// the /usage endpoint. Composes the base provider rather than inheriting it.
internal sealed class OpenCodeGoProvider : ILLMProvider, IUsageReporter
{
    private readonly OpenAICompatibleProvider _inner;
    private readonly HttpClient _client;
    private readonly Uri _usageEndpoint;
    private readonly IApiKeySource _apiKeySource;

    public OpenCodeGoProvider(OpenAICompatibleOptions options, HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(options);
        _inner = new OpenAICompatibleProvider(options, client);
        _client = client;
        _usageEndpoint = HttpStreaming.EndpointUrl(options.BaseUrl, "usage", options.AllowInsecureLocalhost);
        _apiKeySource = options.ApiKeySource;
    }

    public string Id => _inner.Id;

    public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
        _inner.ListModels(cancellationToken);

    public IReadOnlyList<LLMModel> SeedModels() => _inner.SeedModels();

    public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken) =>
        _inner.Call(request, cancellationToken);

    public async Task<SubscriptionUsage> Usage(CancellationToken cancellationToken)
    {
        var body = await HttpStreaming
            .Get(
                _client,
                _usageEndpoint,
                await AuthHeaders(cancellationToken).ConfigureAwait(false),
                HttpStreaming.RequestTimeout,
                HttpStreaming.MaxErrorBytes,
                cancellationToken)
            .ConfigureAwait(false);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var useBalance = JsonRead.Bool(root, "useBalance");

        var primary = Window(root, "rollingUsage");
        UsageWindow? secondary = null;

        if (Window(root, "weeklyUsage") is { } weekly)
        {
            if (primary is null)
            {
                primary = weekly;
            }
            else
            {
                secondary = weekly;
            }
        }

        primary ??= Window(root, "monthlyUsage");

        UsageCredits? credits = null;

        if (useBalance && primary is not null)
        {
            var remaining = Math.Max(0, 100 - primary.UsedPercent);
            credits = new UsageCredits(true, $"{remaining:0}% remaining");
        }

        return new SubscriptionUsage { PrimaryWindow = primary, SecondaryWindow = secondary, Credits = credits };
    }

    private static UsageWindow? Window(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var usedPercent = JsonRead.Number(window, "usedPercent");

        if (usedPercent == 0)
        {
            var remaining = JsonRead.Number(window, "remainingPercent");

            if (remaining > 0)
            {
                usedPercent = Math.Clamp(100 - remaining, 0, 100);
            }
        }

        var resetAt = ParseReset(JsonRead.String(window, "resetAt"));

        if (resetAt is null)
        {
            return null;
        }

        return new UsageWindow(usedPercent, resetAt.Value, (long)JsonRead.Number(window, "window"));
    }

    private static DateTimeOffset? ParseReset(string value)
    {
        if (value.Length == 0)
        {
            return DateTimeOffset.UnixEpoch;
        }

        return DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    private async Task<Dictionary<string, string>> AuthHeaders(CancellationToken cancellationToken)
    {
        var apiKey = await _apiKeySource.ApiKey(cancellationToken).ConfigureAwait(false);

        if (apiKey.Length == 0)
        {
            throw new LLMProviderException($"provider: \"{Id}\" has no API key");
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Authorization"] = "Bearer " + apiKey,
        };
    }
}
