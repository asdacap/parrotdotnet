using System.Text.Json;
using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class WaitAgentTool(AgentRegistry agents) : ITool
{
    public string Name => "wait_agent";

    public string Description =>
        "Wait for a child agent session to complete, yielding if the requested period elapses. "
        + "session_id accepts the canonical child session ID or friendly name. Waiting never stops the agent.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"session_id":{"type":"string","minLength":1,"description":"Child agent session ID or friendly name."},"yield_after_ms":{"type":"integer","minimum":0,"description":"Yield if the agent has not completed after this many milliseconds. Zero or omitted waits indefinitely."}},"required":["session_id"],"additionalProperties":false}
        """;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        string sessionId;
        int yieldAfterMilliseconds;

        try
        {
            using var arguments = new ToolArguments(argumentsJson);
            sessionId = arguments.RequiredString("session_id");
            yieldAfterMilliseconds = arguments.OptionalInt("yield_after_ms") ?? 0;
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
            return (await agents.Get(sessionId).Wait(
                yieldAfterMilliseconds,
                cancellationToken).ConfigureAwait(false)).Format();
        }
        catch (AgentRegistryException failure)
        {
            return $"error: {failure.Message}";
        }
    }
}
