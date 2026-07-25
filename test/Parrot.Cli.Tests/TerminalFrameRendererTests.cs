using System.Text;
using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class TerminalFrameRendererTests
{
    [Test]
    public async Task Draw_and_clear_emit_exact_frame_and_caret_bytes(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 12, new TerminalPalette(false));
        var frame = new TerminalFrame(
            ["live\u001b[2J"],
            new SpinnerValue("thinking", 0),
            new ModelineValue("chat", string.Empty, "model"),
            new PromptValue("> ", "ab\n界x", 1));

        await renderer.Draw(frame, cancellationToken);
        var boundary = output.GetStringBuilder().Length;
        await renderer.Clear(cancellationToken);
        var rendered = output.ToString();

        var expectedDraw =
            "\u001b[?25l\u001b[?7l\u001b[2Klive[2J\r\n\u001b[2K⠋ thinking\r\n" +
            "\u001b[2Kchat   model\r\n\u001b[2K> ab\r\n\u001b[2K界x" +
            "\u001b[1A\r\u001b[3C\u001b[?7h\u001b[?25h";
        var expectedClear =
            "\u001b[?25l\u001b[?7l\u001b[1B\r\u001b[4A" +
            "\u001b[2K\r\n\u001b[2K\r\n\u001b[2K\r\n\u001b[2K\r\n\u001b[2K" +
            "\u001b[4A\r\u001b[?7h\u001b[?25h";

        _ = await Assert.That(rendered[..boundary]).IsEqualTo(expectedDraw);
        _ = await Assert.That(rendered[boundary..]).IsEqualTo(expectedClear);
        _ = await Assert.That(rendered).DoesNotContain("\u001b[?1049");
    }

    [Test]
    public async Task Full_width_modeline_is_drawn_without_terminal_autowrap(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 8, new TerminalPalette(false));

        await renderer.Draw(
            new TerminalFrame(
                [],
                null,
                new ModelineValue("chat", string.Empty, "model"),
                new PromptValue("> ", string.Empty, 0)),
            cancellationToken);
        await renderer.Clear(cancellationToken);

        var rendered = output.ToString();
        _ = await Assert.That(rendered).Contains("\u001b[?7l\u001b[2Kmodel\r\n");
        _ = await Assert.That(Count(rendered, "\u001b[?7l")).IsEqualTo(2);
        _ = await Assert.That(Count(rendered, "\u001b[?7h")).IsEqualTo(2);
    }

    [Test]
    public async Task Live_rows_have_a_full_width_distinct_background(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 8, new TerminalPalette(true));

        await renderer.Draw(
            new TerminalFrame(
                ["busy"],
                null,
                new ModelineValue("chat", string.Empty, "model"),
                new PromptValue("> ", string.Empty, 0)),
            cancellationToken);

        _ = await Assert.That(output.ToString()).Contains(
            "\u001b[48;5;236m\u001b[2K\u001b[0m\u001b[48;5;236m\u001b[38;5;252mbusy\u001b[0m");
    }

    [Test]
    public async Task Flushing_activities_redraws_the_live_frame(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 24, new TerminalPalette(false));
        var initial = new TerminalFrame(
            ["running"],
            null,
            new ModelineValue("build", "working", "model"),
            new PromptValue("> ", "edit", 2));
        var redrawn = new TerminalFrame(
            [],
            null,
            new ModelineValue("build", "working", "model"),
            new PromptValue("> ", "edit", 2));

        await renderer.Draw(initial, cancellationToken);
        var boundary = output.GetStringBuilder().Length;
        await renderer.FlushActivitiesAndDraw(["+ shell finished"], redrawn, cancellationToken);
        var flushed = output.ToString()[boundary..];

        _ = await Assert.That(flushed).Contains("+ shell finished\r\n");
        _ = await Assert.That(flushed).Contains("build");
        _ = await Assert.That(flushed).Contains("> edit");
        _ = await Assert.That(flushed).EndsWith("\r\u001b[4C\u001b[?7h\u001b[?25h");
    }

    [Test]
    public async Task Concurrent_draws_are_serialized(CancellationToken cancellationToken)
    {
        using var output = new TrackingTextWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 80, new TerminalPalette(false));
        var frame = new TerminalFrame(
            [],
            null,
            new ModelineValue("chat", string.Empty, "model"),
            new PromptValue("> ", string.Empty, 0));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => renderer.Draw(frame, cancellationToken)));

        _ = await Assert.That(output.MaximumConcurrentWrites).IsEqualTo(1);
    }

    private static int Count(string value, string part)
    {
        var count = 0;
        var start = 0;
        while ((start = value.IndexOf(part, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += part.Length;
        }

        return count;
    }

    private static class InterlockedExtensions
    {
        internal static int Max(ref int location, int value)
        {
            var current = Volatile.Read(ref location);
            while (current < value)
            {
                var exchanged = Interlocked.CompareExchange(ref location, value, current);
                if (exchanged == current)
                {
                    return value;
                }

                current = exchanged;
            }

            return current;
        }
    }

    private sealed class TrackingTextWriter : TextWriter
    {
        private int _activeWrites;
        private int _maximumConcurrentWrites;

        public override Encoding Encoding => Encoding.UTF8;

        internal int MaximumConcurrentWrites => _maximumConcurrentWrites;

        public override async Task WriteAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _activeWrites);
            _ = InterlockedExtensions.Max(ref _maximumConcurrentWrites, active);
            try
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
            }
            finally
            {
                _ = Interlocked.Decrement(ref _activeWrites);
            }
        }
    }
}
