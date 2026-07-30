using System.ComponentModel;
using System.Text.Json.Serialization;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

[ToolInputModel(AdditionalPropertiesPolicy.Closed)]
internal sealed partial class AgentSendToolInput
{
    [Description("Exact canonical spawned-agent session ID; or, for an agent with a registered direct parent, the case-sensitive literal 'parent', actual parent ID, or actual parent friendly name; or a direct-child friendly name. Resolution follows that precedence.")]
    [JsonPropertyName("session_id")]
    [ToolMinLength(1)]
    [ToolRequired]
    public string? SessionId { get; init; }

    [Description("Message to send.")]
    [JsonPropertyName("message")]
    [ToolMinLength(1)]
    [ToolRequired]
    public string? Message { get; init; }
}
