using Parrot.Cli.Enhanced;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class EnhancedTerminalFoundationTests
{
    [Test]
    public async Task Runtime_usage_aggregates_latest_root_and_descendant_deltas_once()
    {
        var tracker = new RuntimeUsageTracker();
        tracker.Observe(new Event { AgentSessionId = "root", TurnStarted = new TurnStarted() });
        tracker.Observe(new Event
        {
            AgentSessionId = "child",
            AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = "worker" },
        });
        tracker.Observe(new Event
        {
            AgentSessionId = "root",
            AgentStatisticsUpdated = new AgentStatisticsUpdatedEvent
            {
                InputTokens = 2_400,
                CachedInputTokens = 1_600,
                OutputTokens = 600,
                ContextSize = 3_000,
                ContextLimit = 128_000,
                InputCost = 0.5,
                OutputCost = 1,
            },
        });
        tracker.Observe(new Event
        {
            AgentSessionId = "child",
            AgentStatisticsUpdated = new AgentStatisticsUpdatedEvent
            {
                InputTokens = 100,
                CachedInputTokens = 25,
                OutputTokens = 50,
                InputCost = 0.125,
            },
        });

        var usage = tracker.Current;
        _ = await Assert.That(usage.FormatTokens()).IsEqualTo("+2.5ki +650o (+65.00% cache)");
        _ = await Assert.That(usage.FormatContext()).IsEqualTo("3k/128k");
        _ = await Assert.That(usage.FormatCost()).IsEqualTo("$1.63");

        tracker.Observe(new Event
        {
            AgentSessionId = "child",
            AgentStatisticsUpdated = new AgentStatisticsUpdatedEvent
            {
                InputTokens = 100,
                CachedInputTokens = 25,
                OutputTokens = 50,
                InputCost = 0.125,
            },
        });
        _ = await Assert.That(tracker.Current).IsEqualTo(usage);
    }

    [Test]
    public async Task Roles_use_hanging_prefixes_and_thin_modeline_geometry()
    {
        var context = new ScrollbackRenderContext(8, new TerminalPalette(false));
        var user = ImmediateScrollbackValue.User("abcdef界").Render(context);
        var assistant = ImmediateScrollbackValue.Assistant("abcdef界").Render(context);

        _ = await Assert.That(string.Join('|', user)).IsEqualTo("◆ abcdef|  界");
        _ = await Assert.That(string.Join('|', assistant)).IsEqualTo("● abcdef|  界");
        _ = await Assert.That(new ModelineValue("build", "working", "provider/model").Render(32))
            .IsEqualTo("─ mode: build ─ provider/model ");
        _ = await Assert.That(new ModelineValue("build", "working", "model").Render(1)).IsEqualTo("─");
        _ = await Assert.That(new ModelineValue("build", "working", "model").Render(2)).IsEqualTo("─");

        var colorPalette = new TerminalPalette(true);
        _ = await Assert.That(colorPalette.Prompt.Start).IsEqualTo("\u001b[48;5;236m\u001b[32m\u001b[1m");
        _ = await Assert.That(colorPalette.UserMessage.Start).IsEqualTo("\u001b[32m");
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
        var plain = new DiffScrollbackValue("changed files", source)
            .Render(new ScrollbackRenderContext(24, new TerminalPalette(false)));
        var colored = new DiffScrollbackValue("changed files", source)
            .Render(new ScrollbackRenderContext(24, new TerminalPalette(true)));

        _ = await Assert.That(plain.Count).IsEqualTo(DiffScrollbackValue.MaximumRows + 2);
        _ = await Assert.That(string.Join('\n', plain)).Contains("1 -old[2J    value");
        _ = await Assert.That(string.Join('\n', plain)).Contains("… 4 diff rows omitted");
        _ = await Assert.That(string.Join('\n', plain)).DoesNotContain("\u001b");
        _ = await Assert.That(string.Join('\n', colored)).Contains("\u001b[31m1 -old[2J    value\u001b[0m");
        _ = await Assert.That(string.Join('\n', colored)).Contains("\u001b[32m1 +new    value\u001b[0m");
        _ = await Assert.That(plain.All(line => TerminalText.Width(line) <= 24)).IsTrue();
    }

    private sealed class SequencedScrollbackValue(string line, object sequence, bool completed) : IScrollbackItem
    {
        private readonly object _sequence = sequence;

        public bool IsCompleted => completed;

        public bool Continues(IScrollbackItem previous) =>
            previous is SequencedScrollbackValue value && ReferenceEquals(_sequence, value._sequence);

        public IReadOnlyList<string> Render(ScrollbackRenderContext context) => line.Length == 0 ? [] : [line];
    }
}
