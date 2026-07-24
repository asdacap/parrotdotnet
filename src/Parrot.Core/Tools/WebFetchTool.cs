using System.Text.Json;
using Parrot.Web;

namespace Parrot.Tools;

internal sealed class WebFetchTool(WebFetcher fetcher) : ITool
{
    public string Name => "web_fetch";

    public string Description =>
        "Fetch bounded HTTP or HTTPS text with GET or HEAD after exact network permission review.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"url":{"type":"string"},"method":{"type":"string"}},"required":["url"],"additionalProperties":false}
        """;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        try
        {
            var (address, method) = ReadRequest(argumentsJson);
            var result = await fetcher.Fetch(address, method, cancellationToken).ConfigureAwait(false);
            return result.Text;
        }
        catch (JsonException failure)
        {
            return $"error: invalid web fetch arguments: {failure.Message}";
        }
        catch (WebFetchException failure)
        {
            return $"error: {failure.Message}";
        }
    }

    internal static (Uri Address, HttpMethod Method) ReadRequest(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new WebFetchException("web fetch URL is required");
        }

        var address = root.TryGetProperty("url", out var url)
            && url.ValueKind == JsonValueKind.String
            ? url.GetString() ?? string.Empty
            : string.Empty;
        var methodText = root.TryGetProperty("method", out var methodElement)
            && methodElement.ValueKind == JsonValueKind.String
            ? methodElement.GetString() ?? string.Empty
            : string.Empty;
        var method = methodText.Trim().ToUpperInvariant() switch
        {
            "" or "GET" => HttpMethod.Get,
            "HEAD" => HttpMethod.Head,
            _ => throw new WebFetchException("web fetch supports only GET and HEAD"),
        };

        return (WebFetcher.NormalizeAddress(address), method);
    }
}
