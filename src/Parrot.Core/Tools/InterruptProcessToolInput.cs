using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Omitted)]
internal sealed partial class InterruptProcessToolInput
{
    [Description("Reserved shell process name")]
    [JsonPropertyName("name")]
    [ToolRequired]
    public string? Name { get; init; }
}
