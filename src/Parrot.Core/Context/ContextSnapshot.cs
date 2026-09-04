namespace Parrot.Context;

internal sealed record ContextSnapshot(
    long EstimatedTokens,
    int ContextLimit,
    int? UsagePercent,
    int TriggerPercent)
{
    public bool IsAvailable => ContextLimit > 0;

    public bool ExceedsTrigger => IsAvailable
        && EstimatedTokens > ((long)ContextLimit * TriggerPercent) / 100;
}
