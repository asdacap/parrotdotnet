using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Closed)]
internal sealed partial class QuestionToolInput
{
    [Description("Structured questions to ask the user.")]
    [JsonPropertyName("questions")]
    [ToolMinItems(1)]
    [ToolMaxItems(32)]
    [ToolRequired]
    public QuestionWireDefinition[]? Questions { get; init; }
}
