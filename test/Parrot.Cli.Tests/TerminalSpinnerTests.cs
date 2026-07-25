using System.Collections.Concurrent;
using System.Threading.Channels;
using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class TerminalSpinnerTests
{
    [Test]
    public async Task Run_draws_until_cancelled_clears_and_allows_a_later_run(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var delays = Channel.CreateUnbounded<int>();
        var delayCount = 0;
        var indices = new ConcurrentQueue<int>();
        var renderer = new TerminalFrameRenderer(output, static () => 80, new TerminalPalette(false), 10, 12);
        var spinner = new TerminalSpinner(renderer, Delay);

        TerminalFrame Frame(int index)
        {
            indices.Enqueue(index);
            return new TerminalFrame(
                [],
                new SpinnerValue("thinking", index),
                new ModelineValue("chat", string.Empty, "model"),
                new PromptValue("> ", string.Empty, 0));
        }

        async Task Delay(CancellationToken token)
        {
            await delays.Writer.WriteAsync(Interlocked.Increment(ref delayCount), CancellationToken.None);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }

        await spinner.Run(Frame, Lifetime, cancellationToken);
        _ = await Assert.That(output.ToString()).Contains("⠋ thinking");

        var afterFirstRun = output.GetStringBuilder().Length;
        await spinner.Run(Frame, Lifetime, cancellationToken);

        _ = await Assert.That(string.Join(',', indices)).IsEqualTo("0,0");
        _ = await Assert.That(output.GetStringBuilder().Length).IsGreaterThan(afterFirstRun);
        _ = await Assert.That(output.ToString()).EndsWith("\r\u001b[?7h\u001b[?25h");

        async Task Lifetime(Func<Task> stop, CancellationToken token)
        {
            _ = await delays.Reader.ReadAsync(token);
            await stop();
        }
    }
}
