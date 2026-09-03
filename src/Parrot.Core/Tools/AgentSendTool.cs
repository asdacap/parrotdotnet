using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class AgentSendTool(
    AgentResolver resolver,
    AgentSession session) : ITool
{
    private const int MaximumMessageBytes = 32 * 1024;

    public string Name => "agent_send";

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

            if (Encoding.UTF8.GetByteCount(message) > MaximumMessageBytes)
            {
                return ToolResultFormatter.Error(invocation, $"agent message exceeds {MaximumMessageBytes} UTF-8 bytes; split it into smaller messages");
            }
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }

        try
        {
            var target = resolver.ResolveRecipient(sessionId);

            if (!string.Equals(target.SessionId, session.ParentSessionId, StringComparison.Ordinal)
                && !session.ResolvePolicySelection().SecurityProfile.AllowsDelegationTo(
                    target.ResolvePolicySelection().SecurityProfile))
            {
                return ToolResultFormatter.Error(invocation, "cannot delegate to a more permissive agent");
            }

            return (await target.Send(message, cancellationToken).ConfigureAwait(false)).Format();
        }
        catch (AgentRegistryException failure)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("session_id")]
        public string? SessionId { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }
    }
}
