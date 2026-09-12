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

    public long? TriggerTokens { get; init; } = ContextLimit > 0
        ? (long)ContextLimit * TriggerPercent / 100
        : null;

    public bool HasContextLimitOverride { get; init; }

    public bool ExceedsTrigger => ExceedsCompactionTrigger(EstimatedTokens);

    public bool ExceedsCompactionTrigger(long inputTokens) => TriggerTokens is { } triggerTokens
        && inputTokens > triggerTokens;
}
