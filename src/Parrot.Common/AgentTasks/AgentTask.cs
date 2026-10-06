namespace Parrot.AgentTasks;

internal sealed record AgentTask(
    string Name,
    IReadOnlyList<string> Dependencies,
    string Description,
    AgentTaskPayload Payload,
    string AcceptanceCriteria,
    string? Model,
    bool Hidden,
    AgentTaskExecutionStatus State,
    string? Result,
    string? Failure)
{
    internal bool HasSameDefinition(AgentTask other) =>
        Name == other.Name
        && Dependencies.SequenceEqual(other.Dependencies, StringComparer.Ordinal)
        && Description == other.Description
        && Payload.HasSameDefinition(other.Payload)
        && AcceptanceCriteria == other.AcceptanceCriteria
        && Model == other.Model;

    internal bool HasSameVisibility(AgentTask other) =>
        Hidden == other.Hidden && Payload.HasSameVisibility(other.Payload);

    internal AgentTask ApplyVisibilityChanges(AgentTask previous, AgentTask incoming) =>
        this with
        {
            Hidden = previous.Hidden == incoming.Hidden ? Hidden : incoming.Hidden,
            Payload = Payload.ApplyVisibilityChanges(previous.Payload, incoming.Payload),
        };
}
