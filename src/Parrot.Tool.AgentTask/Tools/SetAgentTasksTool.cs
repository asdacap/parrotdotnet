using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Tools;

internal sealed class SetAgentTasksTool(
    ToolWorkspace workspace,
    IAgentTaskService agentTasks,
    IPromptTemplateCatalog promptTemplates) : ITool
{
    public string Name => "set_agent_tasks";

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        Input input;
        try
        {
            input = ToolInputConversion.Deserialize(invocation.ArgumentsJson, AgentTasksToolJsonContext.Default.SetAgentTasksToolInput);
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }

        var hasPath = input.Path.ValueKind != JsonValueKind.Undefined;
        if (hasPath == (input.Tasks.ValueKind != JsonValueKind.Undefined))
        {
            return ToolResultFormatter.Error(invocation, "Tool arguments require exactly one of a nonblank string 'path' or a 'tasks' array.");
        }

        try
        {
            IReadOnlyList<AgentTask> tasks;
            if (hasPath)
            {
                var path = input.Path.ValueKind == JsonValueKind.String ? input.Path.GetString() : null;
                if (string.IsNullOrWhiteSpace(path))
                {
                    return ToolResultFormatter.Error(invocation, "Tool arguments require a nonblank string 'path'.");
                }

                await using var stream = workspace.OpenRegularReadWithoutLinks(path, selection.SecurityProfile);
                using var reader = new StreamReader(stream);
                tasks = AgentTaskParser.ParseArtifact(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Tasks;
            }
            else
            {
                tasks = AgentTaskParser.ParseTaskSet(input.Tasks.GetRawText());
            }

            agentTasks.SetTasks(
                tasks,
                selection,
                new HistoryForkBoundary.BeforeToolBatch(invocation.AssistantSequence, invocation.CallId));
            return promptTemplates.Render(
                "agent-task.set-accepted",
                [new PromptTemplateArgument("count", tasks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResultFormatter.Error(invocation, "access denied");
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or IOException or AgentRegistryException or LLMProviderException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("path")]
        public JsonElement Path { get; init; }

        [JsonPropertyName("tasks")]
        public JsonElement Tasks { get; init; }
    }
}
