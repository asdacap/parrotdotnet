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
}
