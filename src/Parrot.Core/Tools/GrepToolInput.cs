using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Closed)]
internal sealed partial class GrepToolInput
{
    [JsonPropertyName("pattern")]
    [Description(".NET non-backtracking regular expression to search for.")]
    [ToolRequired]
    public string? Pattern { get; init; }

    [JsonPropertyName("path")]
    [Description("Optional workspace-relative file or directory to search.")]
    public string? Path { get; init; }
}
