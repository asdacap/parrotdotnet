using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Closed)]
internal sealed partial class ReadToolInput
{
    [JsonPropertyName("path")]
    [Description("Workspace-relative or authorized absolute file or directory path.")]
    [ToolRequired]
    public string? Path { get; init; }

    [JsonPropertyName("offset")]
    [Description("One-based first line to read.")]
    [ToolMinimum(1)]
    public int? Offset { get; init; }

    [JsonPropertyName("limit")]
    [Description("Maximum number of lines to read.")]
    [ToolMinimum(1)]
    public int? Limit { get; init; }
}
