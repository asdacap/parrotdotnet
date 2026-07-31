using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Llm;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class AgentSpawnTool(
    AgentRegistry agents,
    ModelRouter router,
    AgentSession session,
    AgentTurnSelection selection) : ITool
{
    public string Name => "agent_spawn";

    public string Description =>
        "Start a child agent in an isolated session and return its session ID immediately. Friendly names are unique among this session's direct children. Its terminal result is automatically sent to this session.";

    public string ParametersJson => Input.Descriptor;

    public async Task<ToolExecutionResult> Execute(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        string prompt;
        string requestedProfile;
        string requestedModel;
        string requestedName;

        try
        {
            var input = JsonSerializer.Deserialize(invocation.ArgumentsJson, AgentProcessToolJsonContext.Default.AgentSpawnToolInput)
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

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input
    {
        [Description("The subtask for the child agent")]
        [JsonPropertyName("prompt")]
        [ToolMinLength(1)]
        [ToolRequired]
        public string? Prompt { get; init; }

        [Description("Configured child profile to run")]
        [JsonPropertyName("agent")]
        [ToolMinLength(1)]
        [ToolRequired]
        public string? Agent { get; init; }

        [Description("Optional configured alias or canonical provider/model[/variant] selector; omitted or empty inherits the parent's complete requested selector.")]
        [JsonPropertyName("model")]
        public string? Model { get; init; }

        [Description("Optional friendly name. It is lowercased and sanitized to letters, digits, and hyphens; omitted or empty names are generated.")]
        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }
}
