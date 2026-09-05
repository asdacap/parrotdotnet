using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Protocol;

namespace Parrot.Statuses;

internal sealed class AgentTaskStatusProvider(
    AgentTaskRunCatalog runs,
    PromptTemplateCatalog templates) : IStatusProvider
{
    public string Key => "runtime:agent-tasks";

    public ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshots = runs.Snapshot(query.SessionId);
        if (snapshots.Count == 0)
        {
            return ValueTask.FromResult(StatusObservation.Unavailable);
        }

        var lines = new List<string>();
        foreach (var snapshot in snapshots.OrderBy(item => item.RunId, StringComparer.Ordinal))
        {
            lines.Add(templates.Render("status.runtime.agent-task", [
                new PromptTemplateArgument("run_id", snapshot.RunId),
                new PromptTemplateArgument("display_name", snapshot.DisplayName),
                new PromptTemplateArgument("owner_session_id", snapshot.OwnerAgentSessionId),
                new PromptTemplateArgument("revision", snapshot.Progress.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ]));
            foreach (var node in snapshot.Progress.RootNodes)
            {
                AppendNode(lines, node, 0);
            }
        }

        return ValueTask.FromResult(StatusObservation.AvailableText(string.Join('\n', lines)));
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

    private void AppendNode(List<string> lines, AgentTaskProgressNode node, int depth)
    {
        var indent = new string(' ', (depth + 1) * 2);
        lines.Add(templates.Render("status.runtime.agent-task-node", [
            new PromptTemplateArgument("indent", indent),
            new PromptTemplateArgument("name", node.Name),
            new PromptTemplateArgument("description", node.Description),
            new PromptTemplateArgument("status", Status(node.Status)),
        ]));

        foreach (var child in node.Children)
        {
            AppendNode(lines, child, depth + 1);
        }
    }
}
