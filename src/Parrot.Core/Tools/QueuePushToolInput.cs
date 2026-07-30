using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Closed)]
internal sealed partial class QueuePushToolInput
{
    [Description("Name of the queue to receive the items.")]
    [JsonPropertyName("name")]
    [ToolPattern("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    [ToolRequired]
    public string? Name { get; init; }

    [Description("Strings to push onto the queue.")]
    [JsonPropertyName("items")]
    [ToolRequired]
    public string[]? Items { get; init; }

    [Description("End of the queue onto which the items are pushed.")]
    [JsonPropertyName("direction")]
    [ToolDefaultString("back")]
    [ToolStringEnum("front", "back")]
    public string? Direction { get; init; }
}
