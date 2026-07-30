using System.Text.Json;
using Parrot.Web;

namespace Parrot.Tools;

internal sealed class WebFetchTool(WebFetcher fetcher) : ITool
{
    public string Name => "web_fetch";

    public string Description =>
        "Fetch bounded HTTP or HTTPS text with GET or HEAD after exact network permission review.";

    public string ParametersJson => WebFetchToolInput.Descriptor;

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
        WebFetchToolInput? input;
        try
        {
            input = JsonSerializer.Deserialize(argumentsJson, FileToolJsonContext.Default.WebFetchToolInput);
        }
        catch (JsonException failure) when (string.Equals(failure.Path, "$", StringComparison.Ordinal))
        {
            throw new WebFetchException("web fetch URL is required");
        }

        var address = input?.Url ?? string.Empty;
        var methodText = input?.Method ?? string.Empty;
        var method = methodText.Trim().ToUpperInvariant() switch
        {
            "" or "GET" => HttpMethod.Get,
            "HEAD" => HttpMethod.Head,
            _ => throw new WebFetchException("web fetch supports only GET and HEAD"),
        };

        return (WebFetcher.NormalizeAddress(address), method);
    }
}
