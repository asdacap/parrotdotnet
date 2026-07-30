using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Closed)]
internal sealed partial class WebFetchToolInput
{
    [JsonPropertyName("url")]
    [Description("HTTP or HTTPS URL to fetch.")]
    [ToolRequired]
    public string? Url { get; init; }

    [JsonPropertyName("method")]
    [Description("HTTP method, either GET or HEAD.")]
    public string? Method { get; init; }
}
