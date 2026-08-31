namespace Parrot.AgentTasks;

internal sealed record AcceptanceVerdict(
    AcceptanceVerdictKind Kind,
    string? Evidence,
    string? Feedback,
    AgentTaskPayload? Payload);
