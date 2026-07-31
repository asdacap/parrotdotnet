using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class AgentSendTool(
    AgentRegistry agents,
    AgentSession session,
    AgentTurnSelection caller) : ITool
{
    public string Name => "agent_send";

    public string Description =>
        "Send a message to an agent session. Exact canonical spawned-agent session IDs resolve globally first. For an agent with a registered direct parent, the case-sensitive literal 'parent', actual parent ID, or actual parent friendly name resolves next and takes precedence over a colliding direct-child friendly name. Direct-child friendly names resolve last. Running agents are steered; "
        + "idle agents start a follow-up turn.";

    public string ParametersJson => Input.Descriptor;

    public async Task<ToolExecutionResult> Execute(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        string sessionId;
        string message;

        try
        {
            var input = JsonSerializer.Deserialize(invocation.ArgumentsJson, AgentProcessToolJsonContext.Default.AgentSendToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            sessionId = input.SessionId ?? throw new FormatException("Tool arguments require a string 'session_id'.");
            message = input.Message ?? throw new FormatException("Tool arguments require a string 'message'.");
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return $"error: {failure.Message}";
        }

        try
        {
            var target = agents.GetRecipient(session, sessionId);

            if (!string.Equals(target.SessionId, session.ParentSessionId, StringComparison.Ordinal)
                && !caller.SecurityProfile.AllowsDelegationTo(target.Selection().SecurityProfile))
            {
                return "error: cannot delegate to a more permissive agent";
            }

            return (await target.Send(message, cancellationToken).ConfigureAwait(false)).Format();
        }
        catch (AgentRegistryException failure)
        {
            return $"error: {failure.Message}";
        }
    }

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input
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
}
