using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Tools.Schema;
using Parrot.Web;

namespace Parrot.Tools;

internal sealed partial class WebFetchTool(WebFetcher fetcher) : ITool
{
    public string Name => "web_fetch";

    public string ParametersJson => Input.Descriptor;

    public async Task<ToolExecutionResult> Execute(ToolInvocation invocation, AgentTurnSelection selection, CancellationToken cancellationToken)
    {
        try
        {
            var (address, method) = ReadRequest(invocation.ArgumentsJson);
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
        Input? input;
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

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input
    {
        [JsonPropertyName("url")]
        [ToolRequired]
        public string? Url { get; init; }

        [JsonPropertyName("method")]
        public string? Method { get; init; }
    }
}
