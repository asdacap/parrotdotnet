using System.Text.Json;
using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class AgentSendTool(
    AgentRegistry agents,
    AgentSession session,
    AgentTurnSelection caller) : ITool
{
    public string Name => "agent_send";

    public string Description =>
        "Send a message to an agent session. Exact canonical spawned-agent session IDs resolve globally first. For an agent with a registered direct parent, the case-sensitive literal 'parent', actual parent ID, or actual parent friendly name resolves next and takes precedence over a colliding direct-child friendly name. Direct-child friendly names resolve last. Running agents are steered; "
        + "idle agents start a follow-up turn.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"session_id":{"type":"string","minLength":1,"description":"Exact canonical spawned-agent session ID; or, for an agent with a registered direct parent, the case-sensitive literal 'parent', actual parent ID, or actual parent friendly name; or a direct-child friendly name. Resolution follows that precedence."},"message":{"type":"string","minLength":1,"description":"Message to send."}},"required":["session_id","message"],"additionalProperties":false}
        """;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        string sessionId;
        string message;

        try
        {
            using var arguments = new ToolArguments(argumentsJson);
            sessionId = arguments.RequiredString("session_id");
            message = arguments.RequiredString("message");
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
}
