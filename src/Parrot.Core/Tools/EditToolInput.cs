using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Closed)]
internal sealed partial class EditToolInput
{
    [JsonPropertyName("path")]
    [Description("Path of the existing UTF-8 file to edit.")]
    [ToolRequired]
    [ToolMinLength(1)]
    public string? Path { get; init; }

    [JsonPropertyName("old_string")]
    [Description("Exact, case-sensitive text to replace.")]
    [ToolRequired]
    [ToolMinLength(1)]
    public string? OldString { get; init; }

    [JsonPropertyName("new_string")]
    [Description("Text that replaces each selected match.")]
    [ToolRequired]
    public string? NewString { get; init; }

    [JsonPropertyName("replace_all")]
    [Description("Whether to replace every match instead of requiring exactly one.")]
    [ToolRequired]
    public bool? ReplaceAll { get; init; }
}
