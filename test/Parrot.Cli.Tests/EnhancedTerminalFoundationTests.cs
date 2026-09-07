using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class EnhancedTerminalFoundationTests
{
    [Test]
    [Arguments(1, 0, "0.03i/s 0o/s")]
    [Arguments(0, 1, "0i/s 0.03o/s")]
    [Arguments(30, 60, "1i/s 2o/s")]
    [Arguments(30_000, 3_000_000, "1ki/s 100ko/s")]
    [Arguments(30_000_000, 60_000_000, "1Mi/s 2Mo/s")]
    [Arguments(0, 0, "")]
    public async Task Runtime_usage_formats_rates_invariantly(long input, long output, string expected)
    {
        _ = await Assert.That(RuntimeUsage.FormatRate(new TokenRate((decimal)input / 30, (decimal)output / 30)))
            .IsEqualTo(expected);
    }

    [Test]
    public async Task Runtime_usage_prefers_newer_durable_session_snapshots()
    {
        var tracker = new RuntimeUsageTracker();
        _ = tracker.Observe(new SessionUsageSnapshot
        {
            Revision = 4,
            InputTokens = 2_400,
            CachedInputTokens = 1_600,
            OutputTokens = 600,
            ContextSize = 3_000,
            ContextLimit = 128_000,
            InputCost = 0.5,
            OutputCost = 1,
        });
        _ = tracker.Observe(new SessionUsageSnapshot
        {
            Revision = 5,
            InputTokens = 2_500,
            CachedInputTokens = 1_625,
            OutputTokens = 650,
            ContextSize = 3_100,
            ContextLimit = 128_000,
            InputCost = 0.625,
            OutputCost = 1.25,
        });
        _ = tracker.Observe(new SessionUsageSnapshot
        {
            Revision = 4,
            InputTokens = 9_999,
            OutputTokens = 9_999,
        });

        var usage = tracker.Current;
        _ = await Assert.That(usage.FormatTokens()).IsEqualTo("+2.5ki +650o (+65.00% cache)");
        _ = await Assert.That(usage.FormatContext()).IsEqualTo("3.1k/128k");
        _ = await Assert.That(usage.FormatCost()).IsEqualTo("$1.88");

        tracker.Reset();
        _ = await Assert.That(tracker.Current).IsEqualTo(default);
        _ = await Assert.That(tracker.Observe(new SessionUsageSnapshot
        {
            Revision = 1,
            InputTokens = 10,
            OutputTokens = 2,
        })).IsTrue();
        _ = await Assert.That(tracker.Current.FormatTokens()).IsEqualTo("+10i +2o");
    }

    [Test]
    public async Task Roles_use_hanging_prefixes_and_thin_modeline_geometry()
    {
        var context = new ScrollbackRenderContext(8, new TerminalPalette(false));
        var user = ImmediateScrollbackValue.User("abcdef界").Render(context);
        var assistant = ImmediateScrollbackValue.Assistant("abcdef界").Render(context);

        _ = await Assert.That(string.Join('|', user)).IsEqualTo("◆ abcdef|  界");
        _ = await Assert.That(string.Join('|', assistant)).IsEqualTo("● abcdef|  界");
        ILiveBufferItem providerModeline = new ModelineValue("build", "working", "provider/model");
        ILiveBufferItem modeline = new ModelineValue("build", "working", "model");
        _ = await Assert.That(providerModeline.Render(new LiveBufferRenderContext(32, context.Palette)).Lines.Single().Text)
            .IsEqualTo("─ mode: build ─ provider/model ");
        _ = await Assert.That(modeline.Render(new LiveBufferRenderContext(1, context.Palette)).Lines.Single().Text).IsEqualTo("─");
        _ = await Assert.That(modeline.Render(new LiveBufferRenderContext(2, context.Palette)).Lines.Single().Text).IsEqualTo("─");

        var colorPalette = new TerminalPalette(true);
        _ = await Assert.That(colorPalette.Prompt.Start).IsEqualTo("\u001b[48;5;236m\u001b[32m\u001b[1m");
        _ = await Assert.That(colorPalette.UserMessage.Start).IsEqualTo("\u001b[32m\u001b[1m");
    }

    [Test]
    public async Task Renderer_owns_compact_block_and_role_spacing(CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        var renderer = new TerminalFrameRenderer(output, static () => 40, new TerminalPalette(false), 10, 12, true);
        var frame = new ILiveBufferItem[]
        {
            new ModelineValue("build", string.Empty, "model"),
            new PromptValue("$ ", string.Empty, 0),
        };

        var sequence = new object();
        await renderer.Commit(new SequencedScrollbackValue("open", sequence, false), frame, cancellationToken);
        await renderer.Commit(ImmediateScrollbackValue.Muted(["compact one"]), frame, cancellationToken);
        await renderer.Commit(ImmediateScrollbackValue.Muted(["compact two"]), frame, cancellationToken);
        await renderer.Commit(BlockScrollbackValue.Text("block"), frame, cancellationToken);
        await renderer.Commit(ImmediateScrollbackValue.Muted(["after block"]), frame, cancellationToken);
        await renderer.Commit(ImmediateScrollbackValue.User("question"), frame, cancellationToken);
        await renderer.Commit(ImmediateScrollbackValue.Assistant("answer"), frame, cancellationToken);
        await renderer.Commit(ImmediateScrollbackValue.Muted(["after answer"]), frame, cancellationToken);
        var boundary = output.GetStringBuilder().Length;
        await renderer.Commit(new SequencedScrollbackValue(string.Empty, sequence, true), frame, cancellationToken);

        var transcript = output.ToString()[boundary..];
        _ = await Assert.That(transcript).Contains(string.Join(
            "\r\n",
            "compact one",
            "compact two",
            string.Empty,
            "block",
            string.Empty,
            "after block",
            string.Empty,
            "◆ question",
            string.Empty,
            "● answer",
            string.Empty,
            "after answer",
            string.Empty));
    }

    [Test]
    public async Task Diff_block_is_sanitized_bounded_and_palette_styled()
    {
        var source = string.Join('\n', Enumerable.Range(0, 105).Select(index => index switch
        {
            0 => "--- a/file.cs",
            1 => "+++ b/file.cs",
            2 => "@@ -1 +1 @@",
            3 => "-old\u001b[2J\tvalue",
            4 => "+new\tvalue",
            _ => " context",
        }));
        IScrollbackItem diff = new DiffScrollbackValue("changed files", source);
        var plain = diff.Render(new ScrollbackRenderContext(24, new TerminalPalette(false)));
        var colored = diff.Render(new ScrollbackRenderContext(24, new TerminalPalette(true)));

        _ = await Assert.That(plain.Count).IsEqualTo(DiffScrollbackValue.MaximumRows + 2);
        _ = await Assert.That(string.Join('\n', plain)).Contains("1 -old[2J    value");
        _ = await Assert.That(string.Join('\n', plain)).Contains("… 4 diff rows omitted");
        _ = await Assert.That(string.Join('\n', plain)).DoesNotContain("\u001b");
        _ = await Assert.That(string.Join('\n', colored)).Contains("\u001b[31m1 -old[2J    value\u001b[0m");
        _ = await Assert.That(string.Join('\n', colored)).Contains("\u001b[32m1 +new    value\u001b[0m");
        _ = await Assert.That(plain.All(line => TerminalText.Width(line) <= 24)).IsTrue();
    }

    [Test]
    public async Task Live_items_preserve_stationary_absolute_and_capture_relative_animation_semantics()
    {
        var context = new LiveBufferRenderContext(6, new TerminalPalette(false));
        ILiveBufferItem stationary = new LiveTextValue("still");
        _ = await Assert.That(stationary.Animate(7).Render(context).Lines.Single().Text).IsEqualTo("still");
        _ = await Assert.That(stationary.CaptureAnimation(10).Render(context).Lines.Single().Text).IsEqualTo("still");
        _ = await Assert.That(stationary.AnimateSinceCapture(12).Render(context).Lines.Single().Text).IsEqualTo("still");
        _ = await Assert.That(stationary.Render(context).Lines.Single().Text).IsEqualTo("still");

        ILiveBufferItem marquee = new MarqueeValue("> ", "abcdefghij", 1);
        var captured = marquee.CaptureAnimation(10);
        _ = await Assert.That(marquee.Render(context).Lines.Single().Text).IsEqualTo("> bcde");
        _ = await Assert.That(marquee.Animate(3).Render(context).Lines.Single().Text).IsEqualTo("> defg");
        _ = await Assert.That(captured.AnimateSinceCapture(10).Render(context).Lines.Single().Text).IsEqualTo("> abcd");
        _ = await Assert.That(captured.AnimateSinceCapture(12).Render(context).Lines.Single().Text).IsEqualTo("> cdef");
        _ = await Assert.That(captured.Animate(3).Render(context).Lines.Single().Text).IsEqualTo("> defg");
        _ = await Assert.That(marquee.Render(context).Lines.Single().Text).IsEqualTo("> bcde");

        ILiveBufferItem tool = new ToolLiveValue("work", [], 1);
        _ = await Assert.That(tool.CaptureAnimation(10).Render(context).Lines.Single().Text).IsEqualTo("⠙ work");
        _ = await Assert.That(tool.AnimateSinceCapture(12).Render(context).Lines.Single().Text).IsEqualTo("⠙ work");
        _ = await Assert.That(tool.Render(context).Lines.Single().Text).IsEqualTo("⠙ work");
        _ = await Assert.That(tool.Animate(3).Render(context).Lines.Single().Text).IsEqualTo("⠸ work");
        _ = await Assert.That(tool.Render(context).Lines.Single().Text).IsEqualTo("⠙ work");
    }

    [Test]
    public async Task Streaming_items_preserve_continuation_when_completion_resets_layout()
    {
        var sequence = new StreamingScrollbackSequenceValue();
        var distinctSequence = new StreamingScrollbackSequenceValue();
        var first = sequence.Append(["first"]);
        var continuation = sequence.Append(["next"]);
        var distinct = distinctSequence.Append(["other"]);
        var immediate = ImmediateScrollbackValue.Trusted(["immediate"]);
        var anotherImmediate = ImmediateScrollbackValue.Trusted(["another"]);

        _ = await Assert.That(continuation.Continues(first)).IsTrue();
        _ = await Assert.That(distinct.Continues(first)).IsFalse();
        _ = await Assert.That(first.Continues(distinct)).IsFalse();
        _ = await Assert.That(immediate.Continues(anotherImmediate)).IsFalse();
        _ = await Assert.That(first.Continues(immediate)).IsFalse();
        _ = await Assert.That(immediate.Continues(first)).IsFalse();
        _ = await Assert.That(first.StartsLayout).IsTrue();
        _ = await Assert.That(first.EndsLayout).IsFalse();
        _ = await Assert.That(first.IsCompleted).IsFalse();
        _ = await Assert.That(continuation.StartsLayout).IsFalse();

        var completed = sequence.Complete([]);
        _ = await Assert.That(completed.Continues(first)).IsTrue();
        _ = await Assert.That(completed.IsCompleted).IsTrue();
        _ = await Assert.That(completed.Layout).IsEqualTo(ScrollbackLayout.Assistant);
        _ = await Assert.That(completed.StartsLayout).IsFalse();
        _ = await Assert.That(completed.EndsLayout).IsTrue();

        var emptyCompletion = sequence.Complete([]);
        _ = await Assert.That(emptyCompletion.Continues(completed)).IsTrue();
        _ = await Assert.That(emptyCompletion.IsCompleted).IsTrue();
        _ = await Assert.That(emptyCompletion.Layout).IsEqualTo(ScrollbackLayout.Compact);
        _ = await Assert.That(emptyCompletion.StartsLayout).IsFalse();
        _ = await Assert.That(emptyCompletion.EndsLayout).IsFalse();

        var restarted = sequence.Append(["restarted"]);
        _ = await Assert.That(restarted.Continues(completed)).IsTrue();
        _ = await Assert.That(restarted.Layout).IsEqualTo(ScrollbackLayout.Assistant);
        _ = await Assert.That(restarted.StartsLayout).IsTrue();
        _ = await Assert.That(restarted.EndsLayout).IsFalse();
        _ = await Assert.That(restarted.IsCompleted).IsFalse();
    }

    private sealed class SequencedScrollbackValue(string line, object sequence, bool completed) : IScrollbackItem
    {
        public object? SequenceIdentity => sequence;

        public bool IsCompleted => completed;

        public bool Continues(IScrollbackItem previous) =>
            ReferenceEquals(sequence, previous.SequenceIdentity);

        public IReadOnlyList<string> Render(ScrollbackRenderContext context) => line.Length == 0 ? [] : [line];
    }
}
