namespace Parrot.AgentTasks;

internal sealed class AgentTaskPayload
{
    private AgentTaskPayload(string? instruction, IReadOnlyList<AgentTask>? tasks)
    {
        Instruction = instruction;
        Tasks = tasks;
    }

    internal string? Instruction { get; }

    internal IReadOnlyList<AgentTask>? Tasks { get; }

    internal static AgentTaskPayload FromInstruction(string instruction) =>
        new(instruction, null);

    internal static AgentTaskPayload FromTasks(IReadOnlyList<AgentTask> tasks) =>
        new(null, tasks);

    internal bool HasSameDefinition(AgentTaskPayload other) =>
        Instruction == other.Instruction
        && (Tasks is null
            ? other.Tasks is null
            : other.Tasks is not null
                && Tasks.Count == other.Tasks.Count
                && Tasks.Zip(other.Tasks).All(pair => pair.First.HasSameDefinition(pair.Second)));

    internal bool HasSameVisibility(AgentTaskPayload other) =>
        Tasks is null
            ? other.Tasks is null
            : other.Tasks is not null
                && Tasks.Count == other.Tasks.Count
                && Tasks.Zip(other.Tasks).All(pair => pair.First.HasSameVisibility(pair.Second));

    internal AgentTaskPayload ApplyVisibilityChanges(AgentTaskPayload previous, AgentTaskPayload incoming)
    {
        if (Tasks is not { } current || previous.Tasks is not { } before || incoming.Tasks is not { } after)
        {
            return this;
        }

        var changes = before.Zip(after).ToDictionary(pair => pair.Second.Name, StringComparer.Ordinal);
        return FromTasks([.. current.Select(task => changes.TryGetValue(task.Name, out var pair) && task.HasSameDefinition(pair.Second)
            ? task.ApplyVisibilityChanges(pair.First, pair.Second)
            : task)]);
    }
}
