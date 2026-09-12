namespace Parrot.Context;

internal sealed record ContextCompactionPolicy(long? TriggerTokens, long? TargetTokens)
{
    public static ContextCompactionPolicy Resolve(
        ContextSize? contextLimit,
        ContextSize? targetContextSize,
        int contextWindow,
        int inputLimit,
        int triggerPercent,
        int targetPercent)
    {
        var trigger = contextLimit is null
            ? inputLimit > 0 ? (long)inputLimit * triggerPercent / 100 : (long?)null
            : CapTokens(contextLimit.ResolveTokens(contextWindow), inputLimit);
        var target = targetContextSize is not null
            ? CapTokens(targetContextSize.ResolveTokens(contextWindow), inputLimit)
            : contextLimit is null
                ? inputLimit > 0 ? (long)inputLimit * targetPercent / 100 : (long?)null
                : trigger is { } triggerTokens
                    ? ((triggerTokens / 100) * targetPercent) + (((triggerTokens % 100) * targetPercent) / 100)
                    : null;
        return new ContextCompactionPolicy(trigger, target);
    }

    private static long? CapTokens(long? tokens, int inputLimit) => inputLimit > 0 && tokens is { } resolvedTokens
        ? Math.Min(resolvedTokens, inputLimit)
        : tokens;
}
