namespace Parrot.AgentTasks;

internal sealed record AgentTask(
    string Name,
    IReadOnlyList<string> Dependencies,
    string Description,
    AgentTaskPayload Payload,
    string AcceptanceCriteria,
    string? Model);
