using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Config;
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
    EventRepository eventRepository,
    AgentTaskConfig agentTasks) : ITool
{
    public string Name => "run_agent_tasks";

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        Input input;
        try
        {
            input = JsonSerializer.Deserialize(
                invocation.ArgumentsJson,
                RunAgentTasksToolJsonContext.Default.RunAgentTasksToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }

        var hasPathSource = input.Path.ValueKind != JsonValueKind.Undefined;
        var hasArtifactSource = input.Artifact.ValueKind != JsonValueKind.Undefined;
        if (hasPathSource == hasArtifactSource)
        {
            return hasPathSource
                ? ToolResultFormatter.Error(invocation, "Tool arguments require exactly one nonblank 'path' or non-null 'artifact'.")
                : ToolResultFormatter.Error(invocation, "Tool arguments require a string 'path'.");
        }

        string? path = null;
        if (hasPathSource)
        {
            if (input.Path.ValueKind != JsonValueKind.String)
            {
                return ToolResultFormatter.Error(invocation, "Tool arguments require a string 'path'.");
            }

            path = input.Path.GetString();
            if (string.IsNullOrWhiteSpace(path))
            {
                return ToolResultFormatter.Error(invocation, "Tool arguments require a nonblank string 'path'.");
            }
        }

        if (hasArtifactSource && input.Artifact.ValueKind == JsonValueKind.Null)
        {
            return ToolResultFormatter.Error(invocation, "Tool arguments require exactly one nonblank 'path' or non-null 'artifact'.");
        }

        AgentTaskArtifact artifact;
        try
        {
            if (hasArtifactSource)
            {
                if (input.Artifact.ValueKind != JsonValueKind.Object)
                {
                    return ToolResultFormatter.Error(invocation, "artifact must be an object.");
                }

                artifact = AgentTaskParser.ParseArtifact(input.Artifact.GetRawText());
            }
            else if (path is { } selectedPath)
            {
                await using var stream = workspace.OpenRegularReadWithoutLinks(selectedPath, selection.SecurityProfile);
                using var reader = new StreamReader(stream);
                var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                artifact = AgentTaskParser.ParseArtifact(json);
            }
            else
            {
                throw new InvalidOperationException("AgentTask source validation did not select an input.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResultFormatter.Error(invocation, "access denied");
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or IOException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }

        try
        {
            var progress = new AgentTaskProgress(
                eventBroker,
                eventRepository,
                session.SessionId,
                invocation.CallId);
            var runner = new AgentTaskGraphRunner(agents, router, session, selection, progress, agentTasks);
            return (await runner.Run(artifact, cancellationToken).ConfigureAwait(false)).Serialize();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure) when (failure is AgentRegistryException or LLMProviderException or ArgumentException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("path")]
        public JsonElement Path { get; init; }

        [JsonPropertyName("artifact")]
        public JsonElement Artifact { get; init; }
    }
}
