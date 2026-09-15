using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class AgentSendTool(
    AgentIdentity identity,
    IAgentResolver resolver,
    IAgentSession session) : ITool
{
    private const int MaximumMessageBytes = 32 * 1024;

    public string Name => "agent_send";

    public bool IsEnabledAfterInterruption => true;

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        string name;
        string message;

        try
        {
            var input = JsonSerializer.Deserialize(invocation.ArgumentsJson, AgentProcessToolJsonContext.Default.AgentSendToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            name = input.Name ?? throw new FormatException("Tool arguments require a string 'name'.");
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
            var target = resolver.ResolveRecipient(name);

            if (!string.Equals(target.SessionId, identity.ParentSessionId, StringComparison.Ordinal)
                && !session.ResolvePolicySelection().SecurityProfile.AllowsDelegationTo(
                    target.ResolvePolicySelection().SecurityProfile))
            {
                return ToolResultFormatter.Error(invocation, "cannot delegate to a more permissive agent");
            }

            return (await target.SendTextMessage(message, cancellationToken).ConfigureAwait(false)).Format();
        }
        catch (AgentRegistryException failure)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("message")]
        public string? Message { get; init; }
    }
}
