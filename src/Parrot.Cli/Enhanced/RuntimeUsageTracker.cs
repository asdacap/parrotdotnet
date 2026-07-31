using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class RuntimeUsageTracker
{
    private SessionUsageSnapshot? _snapshot;
    private ulong _revision;

    public RuntimeUsage Current => _snapshot is { } snapshot
        ? new RuntimeUsage(
            snapshot.InputTokens,
            snapshot.CachedInputTokens,
            snapshot.OutputTokens,
            snapshot.ContextSize,
            snapshot.ContextLimit,
            snapshot.InputCost + snapshot.OutputCost)
        : default;

    public bool Observe(SessionUsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (_snapshot is not null && snapshot.Revision <= _revision)
        {
            return false;
        }

        _snapshot = snapshot.Clone();
        _revision = snapshot.Revision;
        return true;
    }

    public void Reset()
    {
        _snapshot = null;
        _revision = 0;
    }
}
