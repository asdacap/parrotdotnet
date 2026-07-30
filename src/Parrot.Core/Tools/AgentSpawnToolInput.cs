using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Closed)]
internal sealed partial class AgentSpawnToolInput
{
    [Description("The subtask for the child agent")]
    [JsonPropertyName("prompt")]
    [ToolMinLength(1)]
    [ToolRequired]
    public string? Prompt { get; init; }

    [Description("Configured child profile to run")]
    [JsonPropertyName("agent")]
    [ToolMinLength(1)]
    [ToolRequired]
    public string? Agent { get; init; }

    [Description("Optional configured alias or canonical provider/model[/variant] selector; omitted or empty inherits the parent's complete requested selector.")]
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [Description("Optional friendly name. It is lowercased and sanitized to letters, digits, and hyphens; omitted or empty names are generated.")]
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}
