using Parrot.Protocol;

namespace Parrot.AgentTasks;

internal sealed record AgentTaskArtifact(int SchemaVersion, IReadOnlyList<AgentTask> Tasks)
{
    internal const int Version1 = 1;

    internal string DisplayName => Tasks.Count == 1 ? Tasks[0].Name : $"{Tasks.Count} top-level tasks";

    internal AgentTaskProgressSnapshot CaptureProgress()
    {
        var snapshot = new AgentTaskProgressSnapshot();
        snapshot.RootNodes.Add(Tasks.Select(CaptureProgress));
        return snapshot;
    }

    internal IReadOnlyList<PlanTaskDeclaration> DeclareTasks() => [.. Tasks.Select(DeclareTask)];

    private static AgentTaskProgressNode CaptureProgress(AgentTask task)
    {
        var node = new AgentTaskProgressNode
        {
            Name = task.Name,
            Description = task.Description,
            Status = AgentTaskProgressStatus.Pending,
            Hidden = task.Hidden,
        };

        if (task.Payload.Tasks is { } children)
        {
            node.Children.Add(children.Select(CaptureProgress));
        }

        return node;
    }

    private static PlanTaskDeclaration DeclareTask(AgentTask task)
    {
        var declaration = new PlanTaskDeclaration
        {
            Name = task.Name,
            Status = AgentTaskProgressStatus.Pending,
            Description = task.Description,
            AcceptanceCriteria = task.AcceptanceCriteria,
            Hidden = task.Hidden,
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
            declaration.Children.Tasks.Add(children.Select(DeclareTask));
        }
        else
        {
            throw new InvalidOperationException("A planned task payload is required.");
        }

        return declaration;
    }
}
