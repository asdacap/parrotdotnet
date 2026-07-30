using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Closed)]
internal sealed partial class QueueTakeToolInput
{
    [Description("Name of the queue from which to remove items.")]
    [JsonPropertyName("name")]
    [ToolPattern("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    [ToolRequired]
    public string? Name { get; init; }

    [Description("Maximum number of items to remove.")]
    [JsonPropertyName("count")]
    [ToolDefaultLong(1)]
    [ToolMinimum(1)]
    public int? Count { get; init; }

    [Description("End of the queue from which items are removed.")]
    [JsonPropertyName("direction")]
    [ToolDefaultString("front")]
    [ToolStringEnum("front", "back")]
    public string? Direction { get; init; }

    [Description("Milliseconds to wait for an item when the queue is empty.")]
    [JsonPropertyName("yield_after_ms")]
    [ToolDefaultLong(30_000)]
    [ToolMinimum(0)]
    public long? YieldAfterMilliseconds { get; init; }
}
