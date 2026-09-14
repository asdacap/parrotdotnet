namespace Parrot.AgentTasks;

internal sealed record EffectiveAgentTask(
    string Name,
    IReadOnlyList<string> Dependencies,
    string Description,
    AgentTaskPayload Payload,
    string AcceptanceCriteria,
    string? Model)
{
    internal static EffectiveAgentTask FromArtifact(AgentTask task) =>
        new(task.Name, task.Dependencies, task.Description, task.Payload, task.AcceptanceCriteria, task.Model);

    internal EffectiveAgentTask Apply(AgentTaskPatch patch) => new(
        Name,
        Dependencies,
        patch.Description ?? Description,
        patch.Payload ?? Payload,
        patch.AcceptanceCriteria ?? AcceptanceCriteria,
        patch.Model.IsSpecified ? patch.Model.Value : Model);
}
