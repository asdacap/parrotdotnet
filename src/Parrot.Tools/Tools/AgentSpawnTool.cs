using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Tools;

internal sealed class AgentSpawnTool(
    IAgentSessionScope ownerScope,
    IModelRouter router) : ITool
{
    public string Name => "agent_spawn";

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        string prompt;
        string requestedProfile;
        string requestedModel;
        string requestedName;
        string requestedScope;
        HistoryForkSelection requestedFork;

        try
        {
            var input = JsonSerializer.Deserialize(invocation.ArgumentsJson, AgentProcessToolJsonContext.Default.AgentSpawnToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            prompt = input.Prompt ?? throw new FormatException("Tool arguments require a string 'prompt'.");
            requestedProfile = input.Agent ?? throw new FormatException("Tool arguments require a string 'agent'.");
            requestedModel = input.Model ?? string.Empty;
            requestedName = string.IsNullOrEmpty(input.Name)
                ? throw new FormatException("Tool arguments require a string 'name'.")
                : input.Name;
            requestedScope = input.Scope ?? string.Empty;
            requestedFork = HistoryForkSelection.Parse(input.Fork ?? string.Empty);
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }

        try
        {
            var model = requestedModel.Length == 0
                ? selection.RequestedModel
                : router.Resolve(requestedModel).RequestedSelector;
            var agent = ownerScope.AgentSpawner.SpawnOrResumeScope(new AgentLaunchRequest(
                ownerScope.Session,
                selection,
                requestedProfile,
                model,
                requestedName,
                requestedScope,
                requestedFork,
                new HistoryForkBoundary.BeforeToolBatch(invocation.AssistantSequence, invocation.CallId),
                AgentCompletionDeliveryPolicy.Automatic)).Session;
            _ = await agent.SendTextMessage(prompt, cancellationToken).ConfigureAwait(false);
            return new SpawnAgentResult(agent.Name, agent.Depth).Format();
        }
        catch (Exception failure) when (failure is AgentRegistryException or LLMProviderException or ArgumentException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("prompt")]
        public string? Prompt { get; init; }

        [JsonPropertyName("agent")]
        public string? Agent { get; init; }

        [JsonPropertyName("model")]
        public string? Model { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("scope")]
        public string? Scope { get; init; }

        [JsonPropertyName("fork")]
        public string? Fork { get; init; }
    }
}
