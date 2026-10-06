namespace Parrot.AgentTasks;

internal sealed record AcceptanceVerdict(
    AcceptanceVerdictKind Kind,
    string? Evidence,
    string? Feedback,
    string? ReplacementInstruction,
    string? ReplacementResult);
