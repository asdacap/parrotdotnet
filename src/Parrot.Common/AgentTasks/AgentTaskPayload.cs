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
}
