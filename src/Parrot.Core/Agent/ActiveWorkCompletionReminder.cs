using System.Text;
using Parrot.Config;
using Parrot.Process;
using Parrot.Statuses;

namespace Parrot.Agent;

internal sealed class ActiveWorkCompletionReminder(
    string agentSessionId,
    AgentRegistry registry,
    ShellProcessOwner processes,
    PromptTemplateCatalog promptTemplates)
{
    public string? Build()
    {
        var children = registry.ActiveDirectChildren(agentSessionId);
        var ownedProcesses = processes.Active();

        if (children.Count == 0 && ownedProcesses.Count == 0)
        {
            return null;
        }

        var activeWork = FormatActiveWork(children, ownedProcesses);
        return promptTemplates.Render(
            "agent-session.active-work-reminder",
            [new PromptTemplateArgument("active_work", activeWork)]);
    }

    private static string FormatActiveWork(
        IReadOnlyList<ActiveWorkObservation> children,
        IReadOnlyList<ActiveWorkObservation> ownedProcesses)
    {
        var work = new StringBuilder();
        Append(work, "Running direct subagents", children);
        Append(work, "Running processes", ownedProcesses);
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
