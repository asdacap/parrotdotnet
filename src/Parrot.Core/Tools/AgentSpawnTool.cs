using System.Text.Json;
using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class AgentSpawnTool(AgentRegistry agents, AgentSession session) : ITool
{
    public string Name => "agent_spawn";

    public string Description =>
        "Start a child agent in an isolated session and return its session ID immediately.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"prompt":{"type":"string","minLength":1,"description":"The subtask for the child agent"},"name":{"type":"string","description":"Optional friendly name. It is lowercased and sanitized to letters, digits, and hyphens; omitted or empty names are generated."}},"required":["prompt"],"additionalProperties":false}
        """;

    public Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        string prompt;
        string requestedName;

        try
        {
            using var arguments = new ToolArguments(argumentsJson);
            prompt = arguments.RequiredString("prompt");
            requestedName = arguments.OptionalString("name");
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return Task.FromResult($"error: {failure.Message}");
        }

        try
        {
            var child = agents.Spawn(session, requestedName);
            return Task.FromResult(child.StartChild(prompt).FormatSpawn());
        }
        catch (AgentRegistryException failure)
        {
            return Task.FromResult($"error: {failure.Message}");
        }
    }
}
