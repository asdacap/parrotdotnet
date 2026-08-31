using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class WaitAgentTool(AgentRegistry agents, AgentSession session) : ITool
{
    public string Name => "wait_agent";

    public async Task<ToolExecutionResult> Execute(ToolInvocation invocation, AgentTurnSelection selection, CancellationToken cancellationToken)
    {
        string sessionId;
        int yieldAfterMilliseconds;

        try
        {
            var input = JsonSerializer.Deserialize(invocation.ArgumentsJson, AgentProcessToolJsonContext.Default.WaitAgentToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            sessionId = input.SessionId ?? throw new FormatException("Tool arguments require a string 'session_id'.");
            yieldAfterMilliseconds = input.YieldAfterMilliseconds ?? 0;
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return $"error: {failure.Message}";
        }

        if (sessionId.Length == 0)
        {
            return "error: no child session given";
        }

        if (yieldAfterMilliseconds < 0)
        {
            return "error: yield_after_ms must not be negative";
        }

        try
        {
            return (await agents.GetChild(session, sessionId).Wait(
                yieldAfterMilliseconds,
                cancellationToken).ConfigureAwait(false)).Format();
        }
        catch (AgentRegistryException failure)
        {
            return $"error: {failure.Message}";
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("session_id")]
        public string? SessionId { get; init; }

        [JsonPropertyName("yield_after_ms")]
        public int? YieldAfterMilliseconds { get; init; }
    }
}
