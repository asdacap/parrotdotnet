using Parrot.AgentTasks;

namespace Parrot.Protocol;

public sealed partial class PlanTaskDeclaration
{
    internal static IReadOnlyList<PlanTaskDeclaration> FromPlannedTasks(IReadOnlyList<AgentTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return [.. tasks.Select(FromPlannedTask)];
    }

    private static PlanTaskDeclaration FromPlannedTask(AgentTask task)
    {
        var declaration = new PlanTaskDeclaration
        {
            Name = task.Name,
            Status = AgentTaskProgressStatus.Pending,
            Description = task.Description,
            AcceptanceCriteria = task.AcceptanceCriteria,
        };
        declaration.Dependencies.Add(task.Dependencies);

        if (task.Model is { } model)
        {
            declaration.Model = model;
        }

        if (task.Payload.Instruction is { } instruction)
        {
            declaration.Instruction = instruction;
        }
        else if (task.Payload.Tasks is { } children)
        {
            declaration.Children = new PlanTaskDeclarationChildren();
            declaration.Children.Tasks.Add(children.Select(FromPlannedTask));
        }
        else
        {
            throw new InvalidOperationException("A planned task payload is required.");
        }

        return declaration;
    }
}
