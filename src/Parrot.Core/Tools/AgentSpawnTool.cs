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
        "Start a child agent in an isolated session and return its session ID immediately. Friendly names are unique among this session's direct children. Its terminal result is automatically sent to this session.";

    public string ParametersJson => AgentSpawnToolInput.Descriptor;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        string prompt;
        string requestedProfile;
        string requestedModel;
        string requestedName;

        try
        {
            var input = JsonSerializer.Deserialize(argumentsJson, AgentProcessToolJsonContext.Default.AgentSpawnToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            prompt = input.Prompt ?? throw new FormatException("Tool arguments require a string 'prompt'.");
            requestedProfile = input.Agent ?? throw new FormatException("Tool arguments require a string 'agent'.");
            requestedModel = input.Model ?? string.Empty;
            requestedName = input.Name ?? string.Empty;
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
