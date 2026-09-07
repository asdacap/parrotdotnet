namespace Parrot.Context;

internal sealed record ContextSnapshot(
    long EstimatedTokens,
    int ContextLimit,
    int? UsagePercent,
    int TriggerPercent)
{
    public int InputLimit { get; init; } = ContextLimit;

    public bool IsAvailable => ContextLimit > 0;

    public bool ExceedsInputLimit => InputLimit > 0 && EstimatedTokens > InputLimit;

    public bool ExceedsTrigger => InputLimit > 0
        && EstimatedTokens > ((long)InputLimit * TriggerPercent) / 100;
}
