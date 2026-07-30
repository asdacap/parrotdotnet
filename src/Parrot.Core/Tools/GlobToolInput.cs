using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Closed)]
internal sealed partial class GlobToolInput
{
    [JsonPropertyName("pattern")]
    [Description("Relative workspace glob pattern, including ** for recursive matching.")]
    [ToolRequired]
    public string? Pattern { get; init; }
}
