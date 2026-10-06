namespace Parrot.AgentTasks;

internal sealed record AgentTask(
    string Name,
    IReadOnlyList<string> Dependencies,
    string Description,
    AgentTaskPayload Payload,
    string AcceptanceCriteria,
    string? Model,
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
}
