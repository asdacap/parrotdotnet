namespace Parrot.AgentTasks;

internal sealed record AgentTaskArtifact(int SchemaVersion, IReadOnlyList<AgentTask> Tasks)
{
    internal const int Version1 = 1;

    internal string DisplayName => Tasks.Count == 1 ? Tasks[0].Name : $"{Tasks.Count} top-level tasks";
}
