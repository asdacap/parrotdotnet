namespace Parrot.AgentTasks;

internal sealed record AcceptanceVerdict(
    AcceptanceVerdictKind Kind,
    string? Feedback,
    string? ReplacementInstruction,
    string? ReplacementResult);
