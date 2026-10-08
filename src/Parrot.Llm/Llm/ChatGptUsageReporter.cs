using System.Text.Json;
using Parrot.Llm.Wire;

namespace Parrot.Llm;

internal sealed class ChatGptUsageReporter(
    Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>> resolveHeaders,
    HttpClient client) : IUsageReporter
{
    private const string UsageEndpoint = "https://chatgpt.com/backend-api/wham/usage";

    public async Task<SubscriptionUsage> Usage(CancellationToken cancellationToken)
    {
        var headers = await resolveHeaders(cancellationToken).ConfigureAwait(false);
        var body = await HttpStreaming
            .Get(client, new Uri(UsageEndpoint), headers, HttpStreaming.RequestTimeout, HttpStreaming.MaxErrorBytes, cancellationToken)
            .ConfigureAwait(false);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        UsageWindow? primary = null;
        UsageWindow? secondary = null;

        if (root.TryGetProperty("rate_limit", out var rateLimit) && rateLimit.ValueKind == JsonValueKind.Object)
        {
            primary = Window(rateLimit, "primary_window");
            secondary = Window(rateLimit, "secondary_window");
        }

        UsageCredits? credits = null;

        if (root.TryGetProperty("credits", out var creditsElement) && creditsElement.ValueKind == JsonValueKind.Object)
        {
            var balance = creditsElement.TryGetProperty("balance", out var raw)
                ? raw.GetRawText().Trim('"')
                : string.Empty;
            credits = new UsageCredits(JsonRead.Bool(creditsElement, "has_credits"), balance);
        }

        return new SubscriptionUsage
        {
            PlanType = JsonRead.String(root, "plan_type"),
            PrimaryWindow = primary,
            SecondaryWindow = secondary,
            Credits = credits,
        };
    }

    private static UsageWindow? Window(JsonElement scope, string name)
    {
        if (!scope.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var resetAt = DateTimeOffset.FromUnixTimeSeconds(JsonRead.Long(window, "reset_at"));
        return new UsageWindow(JsonRead.Number(window, "used_percent"), resetAt, JsonRead.Long(window, "limit_window_seconds"));
    }
}
