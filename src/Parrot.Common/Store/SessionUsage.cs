namespace Parrot.Store;

internal sealed record SessionUsage(
    ulong Revision,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ContextSize,
    long ContextLimit,
    double InputCost,
    double OutputCost)
{
    public static SessionUsage Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);
}
