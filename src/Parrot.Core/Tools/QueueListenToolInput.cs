using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Closed)]
internal sealed partial class QueueListenToolInput
{
    [Description("Name of the queue whose idle notifications to configure.")]
    [JsonPropertyName("name")]
    [ToolPattern("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    [ToolRequired]
    public string? Name { get; init; }

    [Description("Whether idle notification delivery is enabled.")]
    [JsonPropertyName("enabled")]
    [ToolDefaultBool(true)]
    public bool? Enabled { get; init; }
}
