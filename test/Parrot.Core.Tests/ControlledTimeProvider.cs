namespace Parrot.Core.Tests;

internal sealed class ControlledTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ControlledTimer> _timers = [];
    private TaskCompletionSource _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ControlledTimer timer;
        lock (_gate)
        {
            timer = new ControlledTimer(this, callback, state, _utcNow + dueTime);
            _timers.Add(timer);
            _ = _timerCreated.TrySetResult();
        }

        return timer;
    }

    public async Task WaitForTimer(CancellationToken cancellationToken)
    {
        Task signal;
        lock (_gate)
        {
            signal = _timers.Any(timer => !timer.Disposed) ? Task.CompletedTask : _timerCreated.Task;
        }

        await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Advance(TimeSpan duration)
    {
        ControlledTimer[] due;
        lock (_gate)
        {
            _utcNow += duration;
            _timestamp += duration.Ticks;
            due = [.. _timers.Where(timer => !timer.Disposed && timer.Due <= _utcNow)];
            _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class ControlledTimer(
        ControlledTimeProvider owner,
        TimerCallback callback,
        object? state,
        DateTimeOffset due) : ITimer
    {
        public DateTimeOffset Due { get; private set; } = due;

        public bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = owner._utcNow + dueTime;
            return !Disposed;
        }

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Fire()
        {
            if (!Disposed)
            {
                callback(state);
            }
        }
    }
}
