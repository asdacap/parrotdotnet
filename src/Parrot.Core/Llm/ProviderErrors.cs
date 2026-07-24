using Parrot.Llm.Wire;

namespace Parrot.Llm;

// Classifies structured provider failures. HTTP status and free-text message are
// deliberately ignored for usage-limit detection so a transient 429 cannot
// suspend autonomous work; only structured type/code fields decide. Port of the
// classification half of Go's provider.go.
internal static class ProviderErrors
{
    public static bool IsUsageLimit(string type, string code) =>
        UsageLimitValue(type) || UsageLimitValue(code);

    public static bool IsUsageLimit(Exception failure) =>
        failure switch
        {
            ProviderHttpException http => IsUsageLimit(http.ErrorType, http.ErrorCode),
            ProviderResponseException response => IsUsageLimit(response.ErrorType, response.ErrorCode),
            _ => false,
        };

    public static bool IsEngineOverloaded(string type, string code, string message) =>
        OverloadValue(type) || OverloadValue(code) || RetryableProviderMessage(message);

    public static bool IsEngineOverloaded(Exception failure) =>
        failure switch
        {
            ProviderHttpException http => IsEngineOverloaded(http.ErrorType, http.ErrorCode, http.Detail),
            ProviderResponseException response => IsEngineOverloaded(response.ErrorType, response.ErrorCode, response.Detail),
            _ => false,
        };

    private static bool UsageLimitValue(string value) =>
        Normalize(value) is "usage_limit_reached" or "usage_limit_exceeded"
            or "insufficient_quota" or "billing_hard_limit_reached";

    private static bool OverloadValue(string value) =>
        Normalize(value) is "engine_overloaded_error" or "service_unavailable_error" or "server_is_overloaded";

    private static bool RetryableProviderMessage(string message)
    {
        const string prefix = "an error occurred while processing your request. you can retry your request, "
            + "or contact us through our help center at help.openai.com if the error persists. "
            + "please include the request id ";
        const string suffix = " in your message.";

        var normalized = message.Trim().ToLowerInvariant();

        if (!normalized.StartsWith(prefix, StringComparison.Ordinal)
            || !normalized.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var requestId = normalized[prefix.Length..^suffix.Length].Trim();
        return requestId.Length > 0;
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
}
