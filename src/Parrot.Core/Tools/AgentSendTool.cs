using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class AgentSendTool(
    AgentRegistry agents,
    AgentSession session) : ITool
{
    public string Name => "agent_send";

    public string Description =>
        "Send a message to this agent's direct parent or direct child. Exact canonical session IDs for those recipients resolve first. The case-sensitive literal 'parent', actual parent ID, or actual parent friendly name resolves next and takes precedence over a colliding direct-child friendly name. Direct-child friendly names resolve last. Running agents are steered; "
        + "idle agents start a follow-up turn.";

    public string ParametersJson => Input.Descriptor;

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
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
                && !selection.SecurityProfile.AllowsDelegationTo(target.ResolveSelection().SecurityProfile))
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
        [Description("Exact canonical session ID for this agent's direct parent or direct child; or the case-sensitive literal 'parent', actual parent ID, actual parent friendly name, or a direct-child friendly name. Resolution follows that precedence.")]
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
