using System.Text;
using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Process;
using Parrot.Queues;
using Parrot.Statuses;

namespace Parrot.Agent;

internal sealed class ActiveWorkCompletionReminder(
    IChildRegistry children,
    IProcessOwner processes,
    IAgentQueues queues,
    IPromptTemplateCatalog promptTemplates,
    IAgentTaskRunCatalog? agentTasks)
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

        var reminders = new List<string>();
        if (activeChildren.Length > 0 || ownedProcesses.Count > 0 || ownedAgentTasks.Count > 0)
        {
            var activeWork = FormatActiveWork(
                activeChildren,
                ownedProcesses,
                ownedAgentTasks,
                promptTemplates.Render("agent-session.active-work-agent-tasks-heading", []));
            reminders.Add(promptTemplates.Render(
                "agent-session.active-work-reminder",
                [new PromptTemplateArgument("active_work", activeWork)]));
        }

        var nonemptyQueues = queues.Snapshot().Queues
            .Where(static queue => queue.Size > 0)
            .OrderBy(static queue => queue.Name, StringComparer.Ordinal)
            .Select(queue => promptTemplates.Render(
                "agent-session.nonempty-queue-item",
                [
                    new PromptTemplateArgument("name", queue.Name),
                    new PromptTemplateArgument("size", queue.Size.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ]))
            .ToArray();
        if (nonemptyQueues.Length > 0)
        {
            reminders.Add(promptTemplates.Render(
                "agent-session.nonempty-queues-reminder",
                [new PromptTemplateArgument("queues", string.Join('\n', nonemptyQueues))]));
        }

        return reminders.Count == 0 ? null : string.Join('\n', reminders);
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
