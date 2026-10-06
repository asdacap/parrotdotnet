using Parrot.AgentTasks;
using Parrot.Config;
using Scriban.Runtime;

namespace Parrot.Statuses;

internal sealed class AgentTaskStatusProvider(
    IAgentTaskService agentTasks,
    IPromptTemplateCatalog templates) : IStatusProvider
{
    public string Key => "runtime:agent-tasks";

    public ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var tasks = agentTasks.Snapshot();
        if (tasks.Count == 0)
        {
            return ValueTask.FromResult(StatusObservation.Unavailable);
        }

        var nodes = new ScriptArray();
        foreach (var task in tasks)
        {
            nodes.Add(new ScriptObject
            {
                ["indent"] = "  ",
                ["name"] = task.Name,
                ["description"] = task.Description,
                ["status"] = task.State.ToString().ToLowerInvariant(),
            });
        }

        var model = new ScriptObject
        {
            ["section"] = "agent-tasks",
            ["agents"] = new ScriptArray(),
            ["runs"] = new ScriptArray
            {
                new ScriptObject
                {
                    ["run_id"] = query.SessionId,
                    ["display_name"] = tasks.Count == 1 ? tasks[0].Name : $"{tasks.Count} tasks",
                    ["owner_session_id"] = query.SessionId,
                    ["revision"] = string.Empty,
                    ["nodes"] = nodes,
                },
            },
        };
        return ValueTask.FromResult(StatusObservation.AvailableText(
            templates.RenderStructured("status.runtime", model, cancellationToken)));
    }
}
