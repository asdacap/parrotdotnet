using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class AgentSendTool(
    AgentRegistry agents,
    AgentSession session) : ITool
{
    private const int MaximumMessageBytes = 32 * 1024;

    public string Name => "agent_send";

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

            if (Encoding.UTF8.GetByteCount(message) > MaximumMessageBytes)
            {
                return $"error: agent message exceeds {MaximumMessageBytes} UTF-8 bytes; split it into smaller messages";
            }
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return $"error: {failure.Message}";
        }

        try
        {
            var target = agents.GetRecipient(session, sessionId);

            if (!string.Equals(target.SessionId, session.ParentSessionId, StringComparison.Ordinal)
                && !session.ResolvePolicySelection().SecurityProfile.AllowsDelegationTo(
                    target.ResolvePolicySelection().SecurityProfile))
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
        [JsonPropertyName("session_id")]
        [ToolMinLength(1)]
        [ToolRequired]
        public string? SessionId { get; init; }

        [JsonPropertyName("message")]
        [ToolMinLength(1)]
        [ToolRequired]
        public string? Message { get; init; }
    }
}
