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
        "Send a message to a child agent session. Running agents are steered; idle agents start a follow-up turn.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"session_id":{"type":"string","minLength":1,"description":"Child agent session ID or friendly name."},"message":{"type":"string","minLength":1,"description":"Message to send."}},"required":["session_id","message"],"additionalProperties":false}
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
            var target = agents.Get(sessionId);

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
