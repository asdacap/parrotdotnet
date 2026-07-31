using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Closed)]
internal sealed partial class GlobToolInput
{
    [JsonPropertyName("pattern")]
    [Description("Root-relative glob pattern, including ** for recursive matching.")]
    [ToolRequired]
    public string? Pattern { get; init; }

    [JsonPropertyName("path")]
    [Description("Optional workspace-relative or authorized absolute directory to search.")]
    public string? Path { get; init; }
}
