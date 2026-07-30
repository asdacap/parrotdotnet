using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Omitted)]
internal sealed partial class WaitProcessToolInput
{
    [Description("Reserved shell process name")]
    [JsonPropertyName("name")]
    [ToolRequired]
    public string? Name { get; init; }

    [Description("Return the process name if still running after this many milliseconds")]
    [JsonPropertyName("yield_after_ms")]
    [ToolMinimum(0)]
    public long? YieldAfterMilliseconds { get; init; }
}
