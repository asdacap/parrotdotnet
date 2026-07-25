using System.Text;
using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class TerminalFrameRendererTests
{
    [Test]
    public async Task Draw_and_clear_emit_exact_frame_and_caret_bytes(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 12, static () => 24, new TerminalPalette(false));
        var frame = new TerminalFrame(
            ["live\u001b[2J"],
            new SpinnerValue("thinking", 0),
            new ModelineValue("chat", string.Empty, "model"),
            new PromptValue("> ", "ab\n界x", 1));

        await renderer.Draw(frame, cancellationToken);
        var boundary = output.GetStringBuilder().Length;
        await renderer.Clear(cancellationToken);
        var rendered = output.ToString();

        var draw = rendered[..boundary];
        var clear = rendered[boundary..];

        _ = await Assert.That(Count(draw, "\u001b[2K")).IsEqualTo(24);
        _ = await Assert.That(draw).Contains("\u001b[2Klive[2J     \r\n");
        _ = await Assert.That(draw).Contains("\u001b[2K⠋ thinking  \r\n");
        _ = await Assert.That(draw).Contains("\u001b[2Kchat   model\r\n");
        _ = await Assert.That(draw).Contains("\u001b[2K> ab        \r\n");
        _ = await Assert.That(draw).Contains("\u001b[2K界x         \u001b[1A");
        _ = await Assert.That(Count(clear, "\u001b[2K")).IsEqualTo(24);
        _ = await Assert.That(rendered).DoesNotContain("\u001b[?1049");
    }

    [Test]
    public async Task Full_width_modeline_is_drawn_without_terminal_autowrap(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 8, static () => 24, new TerminalPalette(false));

        await renderer.Draw(
            new TerminalFrame(
                [],
                null,
                new ModelineValue("chat", string.Empty, "model"),
                new PromptValue("> ", string.Empty, 0)),
            cancellationToken);
        await renderer.Clear(cancellationToken);

        var rendered = output.ToString();
        _ = await Assert.That(rendered).Contains("\u001b[2Kmodel   \r\n");
        _ = await Assert.That(Count(rendered, "\u001b[?7l")).IsEqualTo(2);
        _ = await Assert.That(Count(rendered, "\u001b[?7h")).IsEqualTo(2);
    }

    [Test]
    public async Task Live_rows_have_a_full_width_distinct_background(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 8, static () => 24, new TerminalPalette(true));

        await renderer.Draw(
            new TerminalFrame(
                ["busy"],
                null,
                new ModelineValue("chat", string.Empty, "model"),
                new PromptValue("> ", string.Empty, 0)),
            cancellationToken);

        _ = await Assert.That(output.ToString()).Contains(
            "\u001b[2K\u001b[48;5;236m\u001b[0m\u001b[48;5;236m\u001b[38;5;252mbusy\u001b[0m" +
            "\u001b[48;5;236m    \u001b[0m");
    }

    [Test]
    public async Task Flushing_activities_redraws_the_live_frame(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 24, static () => 24, new TerminalPalette(false));
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
    public async Task Committing_user_input_clears_the_owned_frame_before_writing_scrollback(
        CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 12, static () => 24, new TerminalPalette(false));
        await renderer.Draw(
            new TerminalFrame(
                ["working"],
                null,
                new ModelineValue("build", "working", "model"),
                new PromptValue("> ", "first\nsecond", 7)),
            cancellationToken);
        var boundary = output.GetStringBuilder().Length;

        await renderer.CommitUserMessage("› ", "first\nsecond", cancellationToken);

        var committed = output.ToString()[boundary..];
        var user = committed.IndexOf("› first\r\nsecond\r\n", StringComparison.Ordinal);
        var redrawn = committed.IndexOf("working", user, StringComparison.Ordinal);

        _ = await Assert.That(user).IsGreaterThan(0);
        _ = await Assert.That(redrawn).IsGreaterThan(user);
        _ = await Assert.That(Count(committed[..user], "\u001b[2K")).IsEqualTo(24);
    }

    [Test]
    public async Task Updating_input_preserves_the_live_frame(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 24, static () => 24, new TerminalPalette(false));
        await renderer.Draw(
            new TerminalFrame(
                ["tool running"],
                new SpinnerValue("working", 0),
                new ModelineValue("build", "working", "model"),
                new PromptValue("> ", string.Empty, 0)),
            cancellationToken);
        var boundary = output.GetStringBuilder().Length;

        await renderer.UpdatePrompt(new PromptValue("> ", "unmanaged no more", 17), cancellationToken);

        await renderer.Draw(
            new TerminalFrame(
                ["next event"],
                null,
                new ModelineValue("build", "working", "model"),
                new PromptValue("> ", string.Empty, 0)),
            cancellationToken);

        var updated = output.ToString()[boundary..];
        _ = await Assert.That(updated).Contains("tool running");
        _ = await Assert.That(updated).Contains("⠋ working");
        _ = await Assert.That(updated).Contains("next event");
        _ = await Assert.That(Count(updated, "> unmanaged no more")).IsEqualTo(2);
    }

    [Test]
    public async Task Height_bounds_the_character_surface_and_clips_oldest_live_rows(
        CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 8, static () => 4, new TerminalPalette(false));

        await renderer.Draw(
            new TerminalFrame(
                ["oldest", "middle", "newest"],
                null,
                new ModelineValue("chat", string.Empty, "model"),
                new PromptValue("> ", string.Empty, 0)),
            cancellationToken);

        var rendered = output.ToString();
        _ = await Assert.That(Count(rendered, "\u001b[2K")).IsEqualTo(4);
        _ = await Assert.That(rendered).DoesNotContain("oldest");
        _ = await Assert.That(rendered).Contains("middle");
        _ = await Assert.That(rendered).Contains("newest");
        _ = await Assert.That(rendered).Contains("model");
        _ = await Assert.That(rendered).Contains("> ");
    }

    [Test]
    public async Task Concurrent_draws_are_serialized(CancellationToken cancellationToken)
    {
        using var output = new TrackingTextWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 80, static () => 24, new TerminalPalette(false));
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
