using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Tools;

internal sealed class RunAgentTasksTool(
    ToolWorkspace workspace,
    AgentRegistry agents,
    ModelRouter router,
    AgentSession session,
    EventBroker eventBroker,
    EventRepository eventRepository) : ITool
{
    public string Name => "run_agent_tasks";

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        string path;
        try
        {
            var input = JsonSerializer.Deserialize(
                invocation.ArgumentsJson,
                RunAgentTasksToolJsonContext.Default.RunAgentTasksToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            path = input.Path ?? throw new FormatException("Tool arguments require a string 'path'.");
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new FormatException("Tool arguments require a nonblank string 'path'.");
            }
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return $"error: {failure.Message}";
        }

        AgentTaskArtifact artifact;
        try
        {
            await using var stream = workspace.OpenRegularReadWithoutLinks(path, selection.SecurityProfile);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            artifact = AgentTaskParser.ParseArtifact(json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return "error: access denied";
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or IOException)
        {
            return $"error: {failure.Message}";
        }

        try
        {
            var progress = new AgentTaskProgress(
                eventBroker,
                eventRepository,
                session.SessionId,
                invocation.CallId);
            var runner = new AgentTaskGraphRunner(agents, router, session, selection, progress);
            return (await runner.Run(artifact, cancellationToken).ConfigureAwait(false)).Serialize();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure) when (failure is AgentRegistryException or LLMProviderException or ArgumentException)
        {
            return $"error: {failure.Message}";
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("path")]
        public string? Path { get; init; }
    }
}
