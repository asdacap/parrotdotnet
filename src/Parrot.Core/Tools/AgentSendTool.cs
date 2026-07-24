using System.Text;
using System.Text.Json;
using Parrot.Agent;
using Parrot.Protocol;

namespace Parrot.Tools;

internal sealed class AgentSendTool(AgentRegistry agents, AgentSession session) : ITool
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
            if (string.IsNullOrWhiteSpace(message))
            {
                return "error: no message given";
            }

            if (Encoding.UTF8.GetByteCount(message) > 1024 * 1024)
            {
                return "error: agent message exceeds 1048576 bytes";
            }

            using var child = await agents.Get(session, sessionId, cancellationToken).ConfigureAwait(false);
            var messageId = Identifier.MessageId();
            _ = await child.Session.Send(message, messageId, Delivery.Steer, cancellationToken)
                .ConfigureAwait(false);

            if (!child.Running)
            {
                agents.FollowUp(child);
            }

            return new AgentSendResult(
                child.Session.SessionId, child.Session.Name, messageId, FollowUp: !child.Running).Format();
        }
        catch (AgentRegistryException failure)
        {
            return $"error: {failure.Message}";
        }
    }
}
