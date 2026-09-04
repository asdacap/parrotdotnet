using Parrot.AgentTasks;

namespace Parrot.Protocol;

public sealed partial class AgentTaskProgressSnapshot
{
    internal static AgentTaskProgressSnapshot FromPlannedTasks(IReadOnlyList<AgentTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var snapshot = new AgentTaskProgressSnapshot();
        snapshot.RootNodes.Add(tasks.Select(FromPlannedTask));
        return snapshot;
    }

    private static AgentTaskProgressNode FromPlannedTask(AgentTask task)
    {
        var node = new AgentTaskProgressNode
        {
            Name = task.Name,
            Description = task.Description,
            Status = AgentTaskProgressStatus.Pending,
        };

        if (task.Payload.Tasks is { } children)
        {
            node.Children.Add(children.Select(FromPlannedTask));
        }

        return node;
    }
}
