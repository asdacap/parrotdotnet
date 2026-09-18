namespace Parrot.Core.Tests;

internal sealed class ImmediateTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            callback(state);
        }

        return new CompletedTimer();
    }

    private sealed class CompletedTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
