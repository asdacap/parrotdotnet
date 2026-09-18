using System.Globalization;
using System.Text.Json;
using Parrot.Llm.Wire;

namespace Parrot.Llm;

internal sealed class OpenCodeGoUsageReporter(OpenAICompatibleOptions options, HttpClient client) : IUsageReporter
{
    private readonly HttpClient _client = client;
    private readonly Uri _usageEndpoint = HttpStreaming.EndpointUrl(
        options.BaseUrl, "usage", options.AllowInsecureLocalhost, options.AllowInsecureRemote);

    private readonly IApiKeySource _apiKeySource = options.ApiKeySource;
    private readonly string _providerId = options.Id;

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

        var usage = root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object
            ? usageElement
            : root;

        var primary = Window(usage, "rolling");
        UsageWindow? secondary = null;

        if (Window(usage, "weekly") is { } weekly)
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

        primary ??= Window(usage, "monthly");

        if (primary is null)
        {
            var legacyPrimary = Window(root, "rollingUsage");
            UsageWindow? legacySecondary = null;

            if (Window(root, "weeklyUsage") is { } legacyWeekly)
            {
                if (legacyPrimary is null)
                {
                    legacyPrimary = legacyWeekly;
                }
                else
                {
                    legacySecondary = legacyWeekly;
                }
            }

            legacyPrimary ??= Window(root, "monthlyUsage");
            primary = legacyPrimary;
            secondary = legacySecondary;
        }

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

        var usedPercent = JsonRead.Number(window, "percent");
        if (usedPercent == 0)
        {
            usedPercent = JsonRead.Number(window, "usedPercent");
        }

        if (usedPercent == 0)
        {
            var remaining = JsonRead.Number(window, "remainingPercent");

            if (remaining > 0)
            {
                usedPercent = Math.Clamp(100 - remaining, 0, 100);
            }
        }

        var resetAt = ParseReset(JsonRead.String(window, "resetsAt"));
        if (resetAt is null || resetAt.Value == DateTimeOffset.UnixEpoch)
        {
            resetAt = ParseReset(JsonRead.String(window, "resetAt"));
        }

        if (resetAt is null)
        {
            return null;
        }

        return new UsageWindow(usedPercent, resetAt.Value, 0);
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
            throw new LLMProviderException($"provider: \"{_providerId}\" has no API key");
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Authorization"] = "Bearer " + apiKey,
            ["User-Agent"] = $"{BuildInfo.ProductName}/{BuildInfo.Version}",
        };
    }
}
