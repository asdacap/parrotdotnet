using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class RollingTokenRateRefreshLifecycleTests
{
    [Test]
    public async Task Reset_cancels_pending_refresh_before_replacement_can_be_invalidated()
    {
        var time = new RateTimeProvider();
        var window = new RollingTokenRateWindow(time);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invalidations = 0;
        var lifecycle = new RollingTokenRateRefreshLifecycle(
            window,
            _ =>
            {
                invalidations++;
                return Task.CompletedTask;
            },
            async (delay, cancellationToken) =>
            {
                _ = delay;
                _ = waiting.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });

        window.Observe(300, 60);
        lifecycle.EnsureRefreshing(CancellationToken.None);
        await waiting.Task;
        await lifecycle.ResetAsync();
        time.Advance(TimeSpan.FromSeconds(30));

        _ = await Assert.That(invalidations).IsEqualTo(0);
        _ = await Assert.That(window.Current).IsEqualTo(default);
    }

    [Test]
    public async Task Sample_observed_after_reset_boundary_survives_joining_old_refresh()
    {
        var window = new RollingTokenRateWindow(new RateTimeProvider());
        var cancellationReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCancellation = new SemaphoreSlim(0, 1);
        var lifecycle = new RollingTokenRateRefreshLifecycle(
            window,
            static _ => Task.CompletedTask,
            async (delay, cancellationToken) =>
            {
                _ = delay;
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _ = cancellationReached.TrySetResult();
                    await releaseCancellation.WaitAsync(CancellationToken.None);
                    throw;
                }
            });

        window.Observe(300, 60);
        lifecycle.EnsureRefreshing(CancellationToken.None);
        var resetting = lifecycle.ResetAsync();
        await cancellationReached.Task;
        window.Observe(150, 30);
        _ = releaseCancellation.Release();
        await resetting;

        _ = await Assert.That(window.Current).IsEqualTo(new TokenRate(5, 1));
        await lifecycle.ShutdownAsync();
    }

    [Test]
    public async Task Shutdown_cancels_and_joins_outstanding_refresh()
    {
        var window = new RollingTokenRateWindow(new RateTimeProvider());
        var cancellationReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCancellation = new SemaphoreSlim(0, 1);
        var lifecycle = new RollingTokenRateRefreshLifecycle(
            window,
            static _ => Task.CompletedTask,
            async (delay, cancellationToken) =>
            {
                _ = delay;
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _ = cancellationReached.TrySetResult();
                    await releaseCancellation.WaitAsync(CancellationToken.None);
                    throw;
                }
            });

        window.Observe(300, 60);
        lifecycle.EnsureRefreshing(CancellationToken.None);
        var shuttingDown = lifecycle.ShutdownAsync();
        await cancellationReached.Task;
        _ = await Assert.That(shuttingDown.IsCompleted).IsFalse();
        _ = releaseCancellation.Release();
        await shuttingDown;
        _ = await Assert.That(shuttingDown.IsCompletedSuccessfully).IsTrue();
    }

    private sealed class RateTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
