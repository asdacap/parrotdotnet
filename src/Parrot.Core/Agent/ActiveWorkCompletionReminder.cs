using System.Text;
using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Process;
using Parrot.Statuses;

namespace Parrot.Agent;

internal sealed class ActiveWorkCompletionReminder(
    IChildRegistry children,
    ShellProcessOwner processes,
    PromptTemplateCatalog promptTemplates,
    AgentTaskRunCatalog? agentTasks)
{
    public string? Build()
    {
        var activeChildren = children.SnapshotDescendants()
            .Where(session => session.IsActive()
                && string.Equals(session.ParentSessionId, processes.SessionId, StringComparison.Ordinal))
            .Select(static session => new ActiveWorkObservation(
                session.SessionId,
                session.Name,
                ActiveWorkKind.Agent,
                ActiveWorkState.Running))
            .OrderBy(static observation => observation.Id, StringComparer.Ordinal)
            .ToArray();
        var ownedProcesses = processes.Active();
        var ownedAgentTasks = agentTasks?.Active() ?? [];

        if (activeChildren.Length == 0 && ownedProcesses.Count == 0 && ownedAgentTasks.Count == 0)
        {
            return null;
        }

        var activeWork = FormatActiveWork(
            activeChildren,
            ownedProcesses,
            ownedAgentTasks,
            promptTemplates.Render("agent-session.active-work-agent-tasks-heading", []));
        return promptTemplates.Render(
            "agent-session.active-work-reminder",
            [new PromptTemplateArgument("active_work", activeWork)]);
    }

    private static string FormatActiveWork(
        IReadOnlyList<ActiveWorkObservation> children,
        IReadOnlyList<ActiveWorkObservation> ownedProcesses,
        IReadOnlyList<ActiveWorkObservation> ownedAgentTasks,
        string agentTasksHeading)
    {
        var work = new StringBuilder();
        Append(work, "Running direct subagents", children);
        Append(work, "Running processes", ownedProcesses);
        Append(work, agentTasksHeading, ownedAgentTasks);
        return work.ToString();
    }

    private static void Append(
        StringBuilder reminder,
        string heading,
        IReadOnlyList<ActiveWorkObservation> active)
    {
        if (active.Count == 0)
        {
            return;
        }

        _ = reminder.Append('\n').Append(heading).Append(':');
        foreach (var item in active.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            _ = reminder.Append("\n- ").Append(item.Id).Append(" (name: ").Append(item.Name).Append(')');
        }
    }
}
