using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Closed)]
internal sealed partial class ExecCommandToolInput
{
    [Description("The shell command to run")]
    [JsonPropertyName("command")]
    [ToolRequired]
    public string? Command { get; init; }

    [Description("Environment variables for the command. Values override the inherited environment.")]
    [JsonPropertyName("env")]
    [JsonConverter(typeof(ProcessEnvironmentJsonConverter))]
    public Dictionary<string, string>? Environment { get; init; }

    [Description("Name unique among running processes; completed names can be reused and omitted names are generated")]
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [Description("Return the process name if still running after this many milliseconds")]
    [JsonPropertyName("yield_after_ms")]
    [ToolMinimum(0)]
    public long? YieldAfterMilliseconds { get; init; }
}
