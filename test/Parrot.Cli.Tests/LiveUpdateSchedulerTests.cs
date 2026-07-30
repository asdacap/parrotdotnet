using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class LiveUpdateSchedulerTests
{
    [Test]
    public async Task Invalidate_coalesces_signals_until_run_consumes_them(CancellationToken cancellationToken)
    {
        var delayStarted = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task Draw(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _ = drawn.TrySetResult();
            return Task.CompletedTask;
        }

        async Task Delay(TimeSpan interval, CancellationToken token)
        {
            _ = delayStarted.TrySetResult(interval);
            await releaseDelay.Task.WaitAsync(token).ConfigureAwait(false);
        }

        var scheduler = new LiveUpdateScheduler(Draw, Delay);

        _ = await Assert.That(scheduler.Invalidate()).IsTrue();
        _ = await Assert.That(scheduler.Invalidate()).IsFalse();

        var run = scheduler.Run(runCancellation.Token);
        var interval = await delayStarted.Task.WaitAsync(cancellationToken);
        _ = await Assert.That(interval).IsEqualTo(TimeSpan.FromSeconds(1d / 30d));
        _ = releaseDelay.TrySetResult();
        await drawn.Task.WaitAsync(cancellationToken);
        await runCancellation.CancelAsync();
        await run;
    }

    [Test]
    public async Task Run_drains_invalidations_received_during_the_publish_interval(CancellationToken cancellationToken)
    {
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drawn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drawCount = 0;
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task Draw(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _ = Interlocked.Increment(ref drawCount);
            _ = drawn.TrySetResult();
            return Task.CompletedTask;
        }

        async Task Delay(TimeSpan interval, CancellationToken token)
        {
            _ = delayStarted.TrySetResult();
            await releaseDelay.Task.WaitAsync(token).ConfigureAwait(false);
        }

        var scheduler = new LiveUpdateScheduler(Draw, Delay);
        _ = scheduler.Invalidate();
        var run = scheduler.Run(runCancellation.Token);
        await delayStarted.Task.WaitAsync(cancellationToken);

        _ = await Assert.That(scheduler.Invalidate()).IsTrue();
        _ = await Assert.That(scheduler.Invalidate()).IsFalse();
        _ = releaseDelay.TrySetResult();
        await drawn.Task.WaitAsync(cancellationToken);
        await runCancellation.CancelAsync();
        await run;

        _ = await Assert.That(drawCount).IsEqualTo(1);
    }

    [Test]
    public async Task Run_draws_the_latest_state_at_publish_time(CancellationToken cancellationToken)
    {
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drawn = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = "first";
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task Draw(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _ = drawn.TrySetResult(state);
            return Task.CompletedTask;
        }

        async Task Delay(TimeSpan interval, CancellationToken token)
        {
            _ = delayStarted.TrySetResult();
            await releaseDelay.Task.WaitAsync(token).ConfigureAwait(false);
        }

        var scheduler = new LiveUpdateScheduler(Draw, Delay);
        _ = scheduler.Invalidate();
        var run = scheduler.Run(runCancellation.Token);
        await delayStarted.Task.WaitAsync(cancellationToken);

        state = "latest";
        _ = scheduler.Invalidate();
        _ = releaseDelay.TrySetResult();

        _ = await Assert.That(await drawn.Task.WaitAsync(cancellationToken)).IsEqualTo("latest");
        await runCancellation.CancelAsync();
        await run;
    }

    [Test]
    public async Task Run_returns_cleanly_when_its_cancellation_is_requested(CancellationToken cancellationToken)
    {
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var scheduler = new LiveUpdateScheduler(static _ => Task.CompletedTask);

        var run = scheduler.Run(runCancellation.Token);
        await runCancellation.CancelAsync();
        await run;

        _ = await Assert.That(run.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task Run_propagates_draw_exceptions(CancellationToken cancellationToken)
    {
        var expected = new InvalidOperationException("draw failed");
        var scheduler = new LiveUpdateScheduler(_ => Task.FromException(expected), static (_, _) => Task.CompletedTask);
        _ = scheduler.Invalidate();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.Run(cancellationToken));

        _ = await Assert.That(exception).IsSameReferenceAs(expected);
    }
}
