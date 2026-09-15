using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Protocol;
using Scriban.Runtime;

namespace Parrot.Statuses;

internal sealed class AgentTaskStatusProvider(
    IAgentTaskRunCatalog agentTaskRuns,
    IPromptTemplateCatalog templates) : IStatusProvider
{
    public string Key => "runtime:agent-tasks";

    public ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshots = agentTaskRuns.Snapshot();
        if (snapshots.Count == 0)
        {
            return ValueTask.FromResult(StatusObservation.Unavailable);
        }

        var runModels = new ScriptArray();
        foreach (var snapshot in snapshots.OrderBy(item => item.RunId, StringComparer.Ordinal))
        {
            var nodes = new ScriptArray();
            foreach (var node in snapshot.Progress.RootNodes)
            {
                AppendNode(nodes, node, 0);
            }

            runModels.Add(new ScriptObject
            {
                ["run_id"] = snapshot.RunId,
                ["display_name"] = snapshot.DisplayName,
                ["owner_session_id"] = snapshot.OwnerAgentSessionId,
                ["revision"] = snapshot.Progress.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["nodes"] = nodes,
            });
        }

        var model = new ScriptObject
        {
            ["section"] = "agent-tasks",
            ["agents"] = new ScriptArray(),
            ["runs"] = runModels,
        };
        return ValueTask.FromResult(StatusObservation.AvailableText(
            templates.RenderStructured("status.runtime", model, cancellationToken)));
    }

    private static string Status(AgentTaskProgressStatus status) => status switch
    {
        AgentTaskProgressStatus.Pending => "pending",
        AgentTaskProgressStatus.Running => "running",
        AgentTaskProgressStatus.Succeeded => "succeeded",
        AgentTaskProgressStatus.Failed => "failed",
        AgentTaskProgressStatus.Blocked => "blocked",
        AgentTaskProgressStatus.Canceled => "canceled",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown AgentTask progress status."),
    };

    private static void AppendNode(ScriptArray nodes, AgentTaskProgressNode node, int depth)
    {
        nodes.Add(new ScriptObject
        {
            ["indent"] = new string(' ', (depth + 1) * 2),
            ["name"] = node.Name,
            ["description"] = node.Description,
            ["status"] = Status(node.Status),
        });
        foreach (var child in node.Children)
        {
            AppendNode(nodes, child, depth + 1);
        }
    }
}
