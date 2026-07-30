using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Closed)]
internal sealed partial class WaitAgentToolInput
{
    [Description("Child agent session ID or friendly name.")]
    [JsonPropertyName("session_id")]
    [ToolMinLength(1)]
    [ToolRequired]
    public string? SessionId { get; init; }

    [Description("Yield if the agent has not completed after this many milliseconds. Zero or omitted waits indefinitely.")]
    [JsonPropertyName("yield_after_ms")]
    [ToolMinimum(0)]
    public int? YieldAfterMilliseconds { get; init; }
}
