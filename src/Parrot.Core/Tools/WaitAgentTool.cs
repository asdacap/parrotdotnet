using System.Text.Json;
using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class WaitAgentTool(AgentRegistry agents, AgentSession session) : ITool
{
    public string Name => "wait_agent";

    public string Description =>
        "Wait for a child agent session to complete, yielding if the requested period elapses. "
        + "Canonical session IDs resolve globally; friendly names resolve among this session's direct children. Waiting never stops the agent.";

    public string ParametersJson => WaitAgentToolInput.Descriptor;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        string sessionId;
        int yieldAfterMilliseconds;

        try
        {
            var input = JsonSerializer.Deserialize(argumentsJson, AgentProcessToolJsonContext.Default.WaitAgentToolInput)
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
}
