using System.Collections.Concurrent;
using System.Threading.Channels;
using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class TerminalSpinnerTests
{
    [Test]
    public async Task Run_draws_until_cancelled_without_a_final_clear_and_allows_a_later_run(
        CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var delays = Channel.CreateUnbounded<int>();
        var delayCount = 0;
        var indices = new ConcurrentQueue<int>();
        var drawItemCounts = new ConcurrentQueue<int>();
        var renderer = new TerminalFrameRenderer(output, static () => 80, new TerminalPalette(false), 10, 12, true);
        var fixedItems = new ILiveBufferItem[]
        {
            new ModelineValue("chat", string.Empty, "model"),
            new PromptValue("> ", string.Empty, 0),
        };
        var spinner = new TerminalSpinner(
            (items, token) =>
            {
                drawItemCounts.Enqueue(items.Count);
                return renderer.Draw([.. items, .. fixedItems], token);
            },
            Delay);

        ILiveBufferItem Frame(int index)
        {
            indices.Enqueue(index);
            return new SpinnerValue("thinking", index);
        }

        async Task Delay(CancellationToken token)
        {
            await delays.Writer.WriteAsync(Interlocked.Increment(ref delayCount), CancellationToken.None);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }

        await spinner.Run(Frame, Lifetime, cancellationToken);
        _ = await Assert.That(output.ToString()).Contains("⠋ thinking");

        await spinner.Run(Frame, Lifetime, cancellationToken);

        _ = await Assert.That(string.Join(',', indices)).IsEqualTo("0,0");
        _ = await Assert.That(string.Join(',', drawItemCounts)).IsEqualTo("1,1");
        _ = await Assert.That(output.ToString()).Contains("model");
        _ = await Assert.That(output.ToString()).EndsWith("\u001b[?7h\u001b[?25h");

        async Task Lifetime(Func<Task> stop, CancellationToken token)
        {
            _ = await delays.Reader.ReadAsync(token);
            await stop();
        }
    }

    [Test]
    public async Task Run_clears_when_the_lifetime_ends_without_preserving_the_frame(
        CancellationToken cancellationToken)
    {
        var drawItemCounts = new List<int>();
        var spinner = new TerminalSpinner(
            (items, token) =>
            {
                token.ThrowIfCancellationRequested();
                drawItemCounts.Add(items.Count);
                return Task.CompletedTask;
            },
            static token => Task.Delay(Timeout.InfiniteTimeSpan, token));

        await spinner.Run(
            static index => new SpinnerValue("thinking", index),
            static (_, _) => Task.CompletedTask,
            cancellationToken);

        _ = await Assert.That(string.Join(',', drawItemCounts)).IsEqualTo("1,0");
    }
}
