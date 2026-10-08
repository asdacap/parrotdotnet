using System.Globalization;
using System.Text.Json;
using Parrot.Llm.Wire;

namespace Parrot.Llm;

internal sealed class OpenCodeGoUsageReporter(OpenAICompatibleOptions options, HttpClient client) : IUsageReporter
{
    private readonly Uri _usageEndpoint = HttpStreaming.EndpointUrl(
        options.BaseUrl, "usage", options.AllowInsecureLocalhost, options.AllowInsecureRemote);

    private readonly IApiKeySource _apiKeySource = options.ApiKeySource;
    private readonly string _providerId = options.Id;

    public async Task<SubscriptionUsage> Usage(CancellationToken cancellationToken)
    {
        var headers = await HttpStreaming.ResolveBearerHeaders(_apiKeySource, _providerId, cancellationToken).ConfigureAwait(false);
        headers["User-Agent"] = $"{BuildInfo.ProductName}/{BuildInfo.Version}";
        var body = await HttpStreaming
            .Get(
                client,
                _usageEndpoint,
                headers,
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

        var (primary, secondary) = Windows(usage, "rolling", "weekly", "monthly");
        if (primary is null)
        {
            (primary, secondary) = Windows(root, "rollingUsage", "weeklyUsage", "monthlyUsage");
        }

        UsageCredits? credits = null;

        if (useBalance && primary is not null)
        {
            var remaining = Math.Max(0, 100 - primary.UsedPercent);
            credits = new UsageCredits(true, $"{remaining:0}% remaining");
        }

        return new SubscriptionUsage { PrimaryWindow = primary, SecondaryWindow = secondary, Credits = credits };
    }

    private static (UsageWindow? Primary, UsageWindow? Secondary) Windows(
        JsonElement scope,
        string rollingName,
        string weeklyName,
        string monthlyName)
    {
        var primary = Window(scope, rollingName);
        UsageWindow? secondary = null;

        if (Window(scope, weeklyName) is { } weekly)
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

        return (primary ?? Window(scope, monthlyName), secondary);
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
}
