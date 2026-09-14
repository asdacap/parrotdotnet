namespace Parrot.AgentTasks;

internal sealed record AgentTaskPatch(
    string? Description,
    AgentTaskPayload? Payload,
    string? AcceptanceCriteria,
    OptionalValue<string> Model);
