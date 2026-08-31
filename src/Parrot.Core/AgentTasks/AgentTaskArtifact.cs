namespace Parrot.AgentTasks;

internal sealed record AgentTaskArtifact(int SchemaVersion, IReadOnlyList<AgentTask> Tasks)
{
    internal const int Version1 = 1;
}
