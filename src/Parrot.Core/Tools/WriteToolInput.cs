using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Closed)]
internal sealed partial class WriteToolInput
{
    [JsonPropertyName("path")]
    [Description("Path of the file to create or replace.")]
    [ToolRequired]
    [ToolMinLength(1)]
    public string? Path { get; init; }

    [JsonPropertyName("content")]
    [Description("Exact UTF-8 content to write.")]
    [ToolRequired]
    public string? Content { get; init; }
}
