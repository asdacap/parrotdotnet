using System.Text;
using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class TerminalFrameRendererTests
{
    [Test]
    public async Task Draw_and_clear_emit_exact_frame_and_caret_bytes(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 12, new TerminalPalette(false), 10, 12);
        var frame = Items(
            [new LiveTextValue("live\u001b[2J"), new SpinnerValue("thinking", 0)],
            new ModelineValue("chat", string.Empty, "model"),
            new PromptValue("> ", "ab\n界x", 1));

        await renderer.Draw(frame, cancellationToken);
        var boundary = output.GetStringBuilder().Length;
        await renderer.Clear(cancellationToken);
        var rendered = output.ToString();

        var draw = rendered[..boundary];
        var clear = rendered[boundary..];

        _ = await Assert.That(Count(draw, "\u001b[2K")).IsEqualTo(5);
        _ = await Assert.That(draw).Contains("\u001b[2Klive[2J     \r\n");
        _ = await Assert.That(draw).Contains("\u001b[2K⠋ thinking  \r\n");
        _ = await Assert.That(draw).Contains("\u001b[2K─ mo model");
        _ = await Assert.That(draw).Contains("\u001b[2K> ab        \r\n");
        _ = await Assert.That(draw).Contains("\u001b[2K  界x       \u001b[1A");
        _ = await Assert.That(Count(clear, "\u001b[2K")).IsEqualTo(5);
        _ = await Assert.That(rendered).DoesNotContain("\u001b[?1049");
    }

    [Test]
    public async Task First_draw_reserves_rows_before_rendering_the_modeline(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 12, new TerminalPalette(false), 10, 12);

        await renderer.Draw(
            Items([], new ModelineValue("chat", string.Empty, "model"), new PromptValue("> ", string.Empty, 0)),
            cancellationToken);

        var rendered = output.ToString();
        var firstErase = rendered.IndexOf("\u001b[2K", StringComparison.Ordinal);
        _ = await Assert.That(rendered[..firstErase]).Contains("\r\n\u001b[1A\r");
    }

    [Test]
    public async Task Full_width_modeline_is_drawn_without_terminal_autowrap(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 8, new TerminalPalette(false), 10, 12);

        await renderer.Draw(
            Items([], new ModelineValue("chat", string.Empty, "model"), new PromptValue("> ", string.Empty, 0)),
            cancellationToken);
        await renderer.Clear(cancellationToken);

        var rendered = output.ToString();
        _ = await Assert.That(rendered).Contains("\u001b[2K─ model \r\n");
        _ = await Assert.That(Count(rendered, "\u001b[?7l")).IsEqualTo(2);
        _ = await Assert.That(Count(rendered, "\u001b[?7h")).IsEqualTo(2);
    }

    [Test]
    public async Task Live_rows_have_a_full_width_distinct_background(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 8, new TerminalPalette(true), 10, 12);

        await renderer.Draw(
            Items(
                [new LiveTextValue("busy")],
                new ModelineValue("chat", string.Empty, "model"),
                new PromptValue("> ", string.Empty, 0)),
            cancellationToken);

        _ = await Assert.That(output.ToString()).Contains(
            "\u001b[2K\u001b[48;5;236m\u001b[0m\u001b[48;5;236m\u001b[38;5;252mbusy\u001b[0m" +
            "\u001b[48;5;236m    \u001b[0m");
    }

    [Test]
    public async Task Committing_scrollback_redraws_the_replacement_frame(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 24, new TerminalPalette(false), 10, 12);
        var initial = Items(
            [new LiveTextValue("running")],
            new ModelineValue("build", "working", "model"),
            new PromptValue("> ", "edit", 2));
        var redrawn = Items(
            [],
            new ModelineValue("build", "working", "model"),
            new PromptValue("> ", "latest", 6));

        await renderer.Draw(initial, cancellationToken);
        var boundary = output.GetStringBuilder().Length;
        await renderer.Commit(ImmediateScrollbackValue.Trusted(["+ shell finished"]), redrawn, cancellationToken);
        var flushed = output.ToString()[boundary..];

        _ = await Assert.That(flushed).Contains("+ shell finished\r\n");
        _ = await Assert.That(flushed).Contains("build");
        _ = await Assert.That(flushed).Contains("> latest");
        _ = await Assert.That(flushed).DoesNotContain("> edit");
        _ = await Assert.That(flushed).EndsWith("\r\u001b[8C\u001b[?7h\u001b[?25h");
    }

    [Test]
    public async Task Committing_multiline_scrollback_clears_the_owned_frame_before_writing(
        CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 12, new TerminalPalette(false), 10, 12);
        await renderer.Draw(
            Items(
                [new LiveTextValue("working")],
                new ModelineValue("build", "working", "model"),
                new PromptValue("> ", "first\nsecond", 7)),
            cancellationToken);
        var boundary = output.GetStringBuilder().Length;

        await renderer.Commit(
            ImmediateScrollbackValue.Trusted(["› first", "second"]),
            Items(
                [new LiveTextValue("working")],
                new ModelineValue("build", "working", "model"),
                new PromptValue("> ", "first\nsecond", 7)),
            cancellationToken);

        var committed = output.ToString()[boundary..];
        var user = committed.IndexOf("› first\r\nsecond\r\n", StringComparison.Ordinal);
        var redrawn = committed.IndexOf("working", user, StringComparison.Ordinal);

        _ = await Assert.That(user).IsGreaterThan(0);
        _ = await Assert.That(redrawn).IsGreaterThan(user);
        _ = await Assert.That(Count(committed[..user], "\u001b[2K")).IsEqualTo(4);
    }

    [Test]
    public async Task Scrollback_values_render_and_express_sequence_lifecycle()
    {
        var context = new ScrollbackRenderContext(4, new TerminalPalette(false));
        var immediate = ImmediateScrollbackValue.User("› ab界");
        var sequence = new StreamingScrollbackSequenceValue();
        var otherSequence = new StreamingScrollbackSequenceValue();
        var first = sequence.Append(["first"]);
        var final = sequence.Complete([]);
        var other = otherSequence.Append(["other"]);

        _ = await Assert.That(string.Join('|', immediate.Render(context))).IsEqualTo("$ ab|  界");
        _ = await Assert.That(immediate.IsCompleted).IsTrue();
        _ = await Assert.That(immediate.Continues(first)).IsFalse();
        _ = await Assert.That(first.IsCompleted).IsFalse();
        _ = await Assert.That(final.IsCompleted).IsTrue();
        _ = await Assert.That(final.Render(context)).IsEmpty();
        _ = await Assert.That(final.Continues(first)).IsTrue();
        _ = await Assert.That(other.Continues(first)).IsFalse();
    }

    [Test]
    public async Task Sequences_bypass_unrelated_items_and_drain_queued_sequences_recursively(
        CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 40, new TerminalPalette(false), 10, 12);
        var frame = Items([], new ModelineValue("chat", string.Empty, "model"), new PromptValue("> ", string.Empty, 0));
        var sequenceA = new object();
        var sequenceB = new object();

        await renderer.Commit(new TestScrollbackItem("A1", sequenceA, false), frame, cancellationToken);
        await renderer.Commit(new TestScrollbackItem("B1", sequenceB, false), frame, cancellationToken);
        await renderer.Commit(new TestScrollbackItem("C", null, true), frame, cancellationToken);
        await renderer.Commit(new TestScrollbackItem("B2", sequenceB, true), frame, cancellationToken);
        var beforeCompletion = output.ToString();
        await renderer.Commit(new TestScrollbackItem("A2", sequenceA, true), frame, cancellationToken);
        var rendered = output.ToString();

        _ = await Assert.That(beforeCompletion).Contains("A1\r\n");
        _ = await Assert.That(beforeCompletion).DoesNotContain("B1\r\n");
        var a2 = rendered.IndexOf("A2\r\n", StringComparison.Ordinal);
        var b1 = rendered.IndexOf("B1\r\n", StringComparison.Ordinal);
        var b2 = rendered.IndexOf("B2\r\n", StringComparison.Ordinal);
        var c = rendered.IndexOf("C\r\n", StringComparison.Ordinal);
        _ = await Assert.That(a2).IsLessThan(b1);
        _ = await Assert.That(b1).IsLessThan(b2);
        _ = await Assert.That(b2).IsLessThan(c);
    }

    [Test]
    public async Task Empty_completion_and_clear_retain_and_release_pending_scrollback(
        CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 40, new TerminalPalette(false), 10, 12);
        var frame = Items([], new ModelineValue("chat", string.Empty, "model"), new PromptValue("> ", string.Empty, 0));
        var sequence = new object();

        await renderer.Commit(new TestScrollbackItem("open", sequence, false), frame, cancellationToken);
        await renderer.Commit(new TestScrollbackItem("pending", null, true), frame, cancellationToken);
        await renderer.Clear(cancellationToken);
        var beforeCompletion = output.ToString();
        await renderer.Commit(new TestScrollbackItem(string.Empty, sequence, true), frame, cancellationToken);
        var completed = output.ToString()[beforeCompletion.Length..];

        _ = await Assert.That(beforeCompletion).DoesNotContain("pending\r\n");
        _ = await Assert.That(completed).Contains("pending\r\n");
        _ = await Assert.That(completed).DoesNotContain("\r\n\r\n");
    }

    [Test]
    public async Task Draw_replaces_the_live_frame_without_clearing_it_first(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 24, new TerminalPalette(false), 10, 12);
        var frame = Items(
            [new LiveTextValue("working")],
            new ModelineValue("chat", string.Empty, "model"),
            new PromptValue("> ", "draft", 5));

        await renderer.Draw(frame, cancellationToken);
        var boundary = output.GetStringBuilder().Length;
        await renderer.Draw(
            Items(
                [new LiveTextValue("done")],
                new ModelineValue("chat", string.Empty, "model"),
                new PromptValue("> ", "draft", 5)),
            cancellationToken);

        var replacement = output.ToString()[boundary..];
        _ = await Assert.That(Count(replacement, "\u001b[2K")).IsEqualTo(3);
        _ = await Assert.That(replacement).DoesNotContain("\u001b[2K\r\n\u001b[2K");
        _ = await Assert.That(replacement).Contains("done");
    }

    [Test]
    public async Task Draw_removes_rows_when_the_live_frame_shrinks(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 24, new TerminalPalette(false), 10, 12);

        await renderer.Draw(
            Items(
                [new LiveTextValue("first\nsecond")],
                new ModelineValue("chat", string.Empty, "model"),
                new PromptValue("> ", "draft", 5)),
            cancellationToken);
        var boundary = output.GetStringBuilder().Length;
        await renderer.Draw(
            Items(
                [],
                new ModelineValue("chat", string.Empty, "model"),
                new PromptValue("> ", "draft", 5)),
            cancellationToken);

        var replacement = output.ToString()[boundary..];
        _ = await Assert.That(replacement).Contains("\u001b[2M");
        _ = await Assert.That(replacement).DoesNotContain("\u001b[B\r\u001b[2K");
    }

    [Test]
    public async Task Complete_snapshot_updates_input_and_live_frame(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 24, new TerminalPalette(false), 10, 12);
        await renderer.Draw(
            Items(
                [new LiveTextValue("tool running"), new SpinnerValue("working", 0)],
                new ModelineValue("build", "working", "model"),
                new PromptValue("> ", string.Empty, 0)),
            cancellationToken);
        var boundary = output.GetStringBuilder().Length;

        await renderer.Draw(
            Items(
                [new LiveTextValue("tool running"), new SpinnerValue("working", 0)],
                new ModelineValue("build", "working", "model"),
                new PromptValue("> ", "unmanaged no more", 17)),
            cancellationToken);

        await renderer.Draw(
            Items(
                [new LiveTextValue("next event")],
                new ModelineValue("build", "working", "model"),
                new PromptValue("> ", "unmanaged no more", 17)),
            cancellationToken);

        var updated = output.ToString()[boundary..];
        _ = await Assert.That(updated).Contains("tool running");
        _ = await Assert.That(updated).Contains("⠋ working");
        _ = await Assert.That(updated).Contains("next event");
        _ = await Assert.That(Count(updated, "> unmanaged no more")).IsEqualTo(2);
    }

    [Test]
    public async Task Live_row_budget_clips_oldest_activity_without_reducing_input(
        CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 8, new TerminalPalette(false), 2, 12);

        await renderer.Draw(
            Items(
                [new LiveTextValue("oldest\nmiddle"), new LiveTextValue("newest")],
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
    public async Task Input_row_budget_is_independent_and_keeps_the_caret_visible(
        CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 12, new TerminalPalette(false), 2, 2);

        await renderer.Draw(
            Items(
                [new LiveTextValue("live one\nlive two")],
                new ModelineValue("chat", string.Empty, "model"),
                new PromptValue("> ", "one\ntwo\nthree\nfour", 18)),
            cancellationToken);

        var rendered = output.ToString();
        _ = await Assert.That(Count(rendered, "\u001b[2K")).IsEqualTo(5);
        _ = await Assert.That(rendered).Contains("live one");
        _ = await Assert.That(rendered).Contains("live two");
        _ = await Assert.That(rendered).DoesNotContain("> one");
        _ = await Assert.That(rendered).DoesNotContain("two         ");
        _ = await Assert.That(rendered).Contains("three");
        _ = await Assert.That(rendered).Contains("four");
    }

    [Test]
    public async Task Complete_buffer_requires_exactly_one_caret(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 80, new TerminalPalette(false), 10, 12);

        _ = await Assert.That(async () => await renderer.Draw(
            [new ModelineValue("chat", string.Empty, "model")],
            cancellationToken)).Throws<InvalidOperationException>();
        _ = await Assert.That(async () => await renderer.Draw(
            [new PromptValue("> ", string.Empty, 0), new PromptValue("$ ", string.Empty, 0)],
            cancellationToken)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Concurrent_draws_are_serialized(CancellationToken cancellationToken)
    {
        using var output = new TrackingTextWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 80, new TerminalPalette(false), 10, 12);
        var frame = Items(
            [],
            new ModelineValue("chat", string.Empty, "model"),
            new PromptValue("> ", string.Empty, 0));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => renderer.Draw(frame, cancellationToken)));

        _ = await Assert.That(output.MaximumConcurrentWrites).IsEqualTo(1);
    }

    private static IReadOnlyList<ILiveBufferItem> Items(
        IReadOnlyList<ILiveBufferItem> body,
        ModelineValue modeline,
        PromptValue prompt) => [.. body, modeline, prompt];

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

    private sealed class TestScrollbackItem(string line, object? sequence, bool completed) : IScrollbackItem
    {
        private readonly object? _sequence = sequence;

        public bool IsCompleted => completed;

        public bool Continues(IScrollbackItem previous) =>
            _sequence is not null
            && previous is TestScrollbackItem item
            && ReferenceEquals(_sequence, item._sequence);

        public IReadOnlyList<string> Render(ScrollbackRenderContext context) =>
            line.Length == 0 ? [] : [line];
    }

    private sealed class TrackingTextWriter : TextWriter
    {
        private int _activeWrites;
        private int _maximumConcurrentWrites;

        public override Encoding Encoding => Encoding.UTF8;

        internal int MaximumConcurrentWrites => _maximumConcurrentWrites;

        public override async Task WriteAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken)
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
