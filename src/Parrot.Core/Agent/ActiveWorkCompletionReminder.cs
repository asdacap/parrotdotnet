using System.Text;
using Parrot.Process;
using Parrot.Statuses;

namespace Parrot.Agent;

internal sealed class ActiveWorkCompletionReminder(
    string agentSessionId,
    AgentRegistry registry,
    ShellProcessOwner processes)
{
    public string? Build()
    {
        var children = registry.ActiveDirectChildren(agentSessionId);
        var ownedProcesses = processes.Active();

        if (children.Count == 0 && ownedProcesses.Count == 0)
        {
            return null;
        }

        var reminder = new StringBuilder(
            "This turn must not exit while direct subagents or processes are still running.");

        Append(reminder, "Running direct subagents", children);
        Append(reminder, "Running processes", ownedProcesses);

        _ = reminder.Append(
            "\nUse the wait tool until no direct subagent or process remains. "
            + "To interrupt a direct subagent, use agent_send to instruct it to complete. "
            + "To interrupt a process, use interrupt_process. "
            + "Do not finish this turn until all listed work has completed or been interrupted.");
        return reminder.ToString();
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
