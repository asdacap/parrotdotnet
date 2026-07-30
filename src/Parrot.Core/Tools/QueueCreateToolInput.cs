using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Closed)]
internal sealed partial class QueueCreateToolInput
{
    [Description("Queue name containing lowercase ASCII letters, numbers, and hyphens.")]
    [JsonPropertyName("name")]
    [ToolPattern("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    [ToolRequired]
    public string? Name { get; init; }

    [Description("Human-readable description of the queue.")]
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}
