using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Closed)]
internal sealed partial class QuestionWireOption
{
    [Description("Stable identifier for this answer option.")]
    [JsonPropertyName("id")]
    [ToolMinLength(1)]
    [ToolRequired]
    public string? Id { get; init; }

    [Description("Answer text shown to the user.")]
    [JsonPropertyName("label")]
    [ToolMinLength(1)]
    [ToolRequired]
    public string? Label { get; init; }
}
