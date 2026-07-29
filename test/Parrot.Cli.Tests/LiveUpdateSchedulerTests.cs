using System.Collections.Concurrent;
using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class LiveUpdateSchedulerTests
{
    [Test]
    public async Task Coalesces_pending_live_updates_until_the_publish_interval_elapses(CancellationToken cancellationToken)
    {
        var draws = new ConcurrentQueue<string>();
        var started = new TaskCompletionSource<TimeSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            draws.Enqueue(string.Join('|', items.Cast<MarqueeValue>().Select(static item => item.Text)));
            _ = published.TrySetResult();
            return Task.CompletedTask;
        }

        Task Commit(IScrollbackItem item, IReadOnlyList<ILiveBufferItem> items, CancellationToken token) =>
            Task.CompletedTask;

        async Task Delay(TimeSpan interval, CancellationToken token)
        {
            _ = started.TrySetResult(interval);
            await release.Task.WaitAsync(token).ConfigureAwait(false);
        }

        await using var scheduler = new LiveUpdateScheduler(Draw, Commit, Delay);
        await scheduler.Publish(new MarkdownLiveUpdate(null, string.Empty, ["first"]), cancellationToken);
        await scheduler.Publish(new MarkdownLiveUpdate(null, string.Empty, ["second"]), cancellationToken);

        var interval = await started.Task.WaitAsync(cancellationToken);
        _ = await Assert.That(interval).IsEqualTo(TimeSpan.FromSeconds(1d / 30d));
        _ = await Assert.That(draws).IsEmpty();
        _ = release.TrySetResult();
        await published.Task.WaitAsync(cancellationToken);

        _ = await Assert.That(draws.Count).IsEqualTo(1);
        _ = await Assert.That(draws.Single()).IsEqualTo("second");
    }

    [Test]
    public async Task Flush_publishes_the_latest_live_update_without_waiting_for_the_interval(CancellationToken cancellationToken)
    {
        var draws = new ConcurrentQueue<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            draws.Enqueue(string.Join('|', items.Cast<MarqueeValue>().Select(static item => item.Text)));
            return Task.CompletedTask;
        }

        Task Commit(IScrollbackItem item, IReadOnlyList<ILiveBufferItem> items, CancellationToken token) =>
            Task.CompletedTask;

        Task Delay(TimeSpan interval, CancellationToken token) => release.Task.WaitAsync(token);

        await using var scheduler = new LiveUpdateScheduler(Draw, Commit, Delay);
        await scheduler.Publish(new MarkdownLiveUpdate(null, string.Empty, ["latest"]), cancellationToken);
        await scheduler.Flush(cancellationToken);

        _ = await Assert.That(draws.Count).IsEqualTo(1);
        _ = await Assert.That(draws.Single()).IsEqualTo("latest");
    }
}
