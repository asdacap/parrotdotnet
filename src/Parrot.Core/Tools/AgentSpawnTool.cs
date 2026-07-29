using System.Text.Json;
using Parrot.Agent;
using Parrot.Llm;

namespace Parrot.Tools;

internal sealed class AgentSpawnTool(
    AgentRegistry agents,
    ModelRouter router,
    AgentSession session,
    AgentTurnSelection selection) : ITool
{
    public string Name => "agent_spawn";

    public string Description =>
        "Start a child agent in an isolated session and return its session ID immediately.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"prompt":{"type":"string","minLength":1,"description":"The subtask for the child agent"},"agent":{"type":"string","minLength":1,"description":"Configured child profile to run"},"model":{"type":"string","description":"Optional configured alias or canonical provider/model[/variant] selector; omitted or empty inherits the parent's complete requested selector."},"name":{"type":"string","description":"Optional friendly name. It is lowercased and sanitized to letters, digits, and hyphens; omitted or empty names are generated."}},"required":["prompt","agent"],"additionalProperties":false}
        """;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        string prompt;
        string requestedProfile;
        string requestedModel;
        string requestedName;

        try
        {
            using var arguments = new ToolArguments(argumentsJson);
            prompt = arguments.RequiredString("prompt");
            requestedProfile = arguments.RequiredString("agent");
            requestedModel = arguments.OptionalString("model");
            requestedName = arguments.OptionalString("name");
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return $"error: {failure.Message}";
        }

        try
        {
            var model = requestedModel.Length == 0
                ? selection.RequestedModel
                : router.Resolve(requestedModel).RequestedSelector;
            var agent = agents.Spawn(session, selection, requestedProfile, model, requestedName);
            _ = await agent.Send(prompt, cancellationToken).ConfigureAwait(false);
            return new SpawnAgentResult(agent.SessionId, agent.Name, agent.Depth).Format();
        }
        catch (Exception failure) when (failure is AgentRegistryException or LLMProviderException)
        {
            return $"error: {failure.Message}";
        }
    }
}
