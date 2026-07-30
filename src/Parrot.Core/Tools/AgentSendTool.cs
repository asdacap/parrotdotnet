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
        "Send a message to an agent session, including this agent's parent. Canonical session IDs resolve globally; friendly names resolve among this session's direct children, with the parent name taking precedence. Running agents are steered; "
        + "idle agents start a follow-up turn.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"session_id":{"type":"string","minLength":1,"description":"Agent session ID or friendly name. A child can use the parent session ID or name from its context."},"message":{"type":"string","minLength":1,"description":"Message to send."}},"required":["session_id","message"],"additionalProperties":false}
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
