using System.Globalization;
using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Context;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class RawReasoningActivityTests
{
    [Test]
    [Arguments("root", "prepare")]
    [Arguments("root", "completed")]
    [Arguments("root", "summary")]
    [Arguments("child", "prepare")]
    [Arguments("child", "completed")]
    [Arguments("child", "summary")]
    public async Task Raw_reasoning_shows_a_counted_spinner_and_flushes_one_notice(
        string agentKind,
        string trigger,
        CancellationToken cancellationToken)
    {
        var isRoot = string.Equals(agentKind, "root", StringComparison.Ordinal);
        var agentSessionId = isRoot ? "root" : "child";
        const string firstFragment = "reasoning about the problem";
        const string secondFragment = "\u001b[2Jmore reasoning";
        var accumulated = string.Concat(
            TerminalText.Sanitize(firstFragment),
            TerminalText.Sanitize(secondFragment));
        var expectedCount = TokenEstimator.EstimateTokens(accumulated).ToString(CultureInfo.InvariantCulture);

        await using var fixture = new RawReasoningFixture();
        await fixture.View.Render(TurnStart("root"), cancellationToken);
        if (!isRoot)
        {
            await fixture.View.Render(ChildStart("child", "worker"), cancellationToken);
            await fixture.View.Render(TurnStart("child"), cancellationToken);
        }

        await fixture.View.Render(RawChunk(agentSessionId, firstFragment, completed: false), cancellationToken);
        await fixture.View.Render(RawChunk(agentSessionId, secondFragment, completed: false), cancellationToken);
        _ = await Assert.That(fixture.LastDrawn).Contains(LiveLine(isRoot, expectedCount));

        switch (trigger)
        {
            case "prepare":
                await fixture.View.Prepare(PhaseChange(agentSessionId), cancellationToken);
                break;
            case "completed":
                await fixture.View.Render(RawChunk(agentSessionId, string.Empty, completed: true), cancellationToken);
                break;
            default:
                await fixture.View.Render(SummaryChunk(agentSessionId, "# Findings"), cancellationToken);
                break;
        }

        var expectedCommits = string.Equals(trigger, "summary", StringComparison.Ordinal) ? 2 : 1;
        _ = await Assert.That(fixture.Committed).Count().IsEqualTo(expectedCommits);
        _ = await Assert.That(Count(fixture.CommittedText, "Reasoned for ")).IsEqualTo(1);
        _ = await Assert.That(fixture.CommittedText).Contains(NoticeLine(isRoot, expectedCount));
        _ = await Assert.That(fixture.LastDrawn).DoesNotContain("Thinking (");
    }

    [Test]
    public async Task Raw_reasoning_count_grows_with_each_fragment_and_stays_silent_when_nothing_arrived(
        CancellationToken cancellationToken)
    {
        await using var fixture = new RawReasoningFixture();
        await fixture.View.Render(TurnStart("root"), cancellationToken);
        await fixture.View.Prepare(PhaseChange("root"), cancellationToken);
        await fixture.View.Render(SummaryChunk("root", string.Empty), cancellationToken);
        _ = await Assert.That(fixture.Committed).IsEmpty();
        _ = await Assert.That(fixture.LastDrawn).DoesNotContain("Thinking (");

        var accumulated = string.Empty;
        foreach (var fragment in new[] { "first", "\u001b[2Jsecond", " third\n" })
        {
            await fixture.View.Render(RawChunk("root", fragment, completed: false), cancellationToken);
            accumulated += TerminalText.Sanitize(fragment);
            var count = TokenEstimator.EstimateTokens(accumulated).ToString(CultureInfo.InvariantCulture);
            _ = await Assert.That(fixture.LastDrawn).Contains(LiveLine(isRoot: true, count));
        }

        _ = await Assert.That(fixture.Committed).IsEmpty();
    }

    [Test]
    public async Task Raw_reasoning_state_is_isolated_per_agent(CancellationToken cancellationToken)
    {
        await using var fixture = new RawReasoningFixture();
        await fixture.View.Render(TurnStart("root"), cancellationToken);
        await fixture.View.Render(ChildStart("child", "worker"), cancellationToken);
        await fixture.View.Render(TurnStart("child"), cancellationToken);
        await fixture.View.Render(RawChunk("root", "root reasoning", completed: false), cancellationToken);
        await fixture.View.Render(RawChunk("child", "a much longer child reasoning", completed: false), cancellationToken);

        var rootCount = TokenEstimator.EstimateTokens("root reasoning").ToString(CultureInfo.InvariantCulture);
        var childCount = TokenEstimator
            .EstimateTokens("a much longer child reasoning")
            .ToString(CultureInfo.InvariantCulture);
        _ = await Assert.That(fixture.LastDrawn).Contains(LiveLine(isRoot: true, rootCount));
        _ = await Assert.That(fixture.LastDrawn).Contains(LiveLine(isRoot: false, childCount));

        await fixture.View.Prepare(PhaseChange("child"), cancellationToken);
        _ = await Assert.That(fixture.Committed).Count().IsEqualTo(1);
        _ = await Assert.That(fixture.CommittedText).Contains(NoticeLine(isRoot: false, childCount));
        _ = await Assert.That(fixture.CommittedText).DoesNotContain(NoticeLine(isRoot: true, rootCount));
        _ = await Assert.That(fixture.LastDrawn).Contains(LiveLine(isRoot: true, rootCount));
        _ = await Assert.That(fixture.LastDrawn).DoesNotContain(LiveLine(isRoot: false, childCount));

        await fixture.View.Prepare(PhaseChange("root"), cancellationToken);
        _ = await Assert.That(fixture.Committed).Count().IsEqualTo(2);
        _ = await Assert.That(fixture.CommittedText).Contains(NoticeLine(isRoot: true, rootCount));
        _ = await Assert.That(Count(fixture.CommittedText, "Reasoned for ")).IsEqualTo(2);
        _ = await Assert.That(fixture.LastDrawn).DoesNotContain("Thinking (");
    }

    private static string LiveLine(bool isRoot, string count) => isRoot
        ? $"{TerminalIcons.SpinnerFrames[0]} Thinking ({count} tokens)…"
        : $"  {TerminalIcons.SpinnerFrames[0]} [worker] Thinking ({count} tokens)…";

    private static string NoticeLine(bool isRoot, string count) => isRoot
        ? $"{TerminalIcons.Reasoning} Reasoned for {count} tokens…"
        : $"  {TerminalIcons.Reasoning} [worker] Reasoned for {count} tokens…";

    private static Event TurnStart(string agentSessionId) =>
        new() { AgentSessionId = agentSessionId, TurnStarted = new TurnStarted { Model = "model" } };

    private static Event ChildStart(string agentSessionId, string name) =>
        new()
        {
            AgentSessionId = agentSessionId,
            AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = name },
        };

    private static Event RawChunk(string agentSessionId, string fragment, bool completed) =>
        new()
        {
            AgentSessionId = agentSessionId,
            ReasoningChunk = new ReasoningChunk
            {
                Fragment = fragment,
                Kind = ReasoningKind.Raw,
                Completed = completed,
            },
        };

    private static Event SummaryChunk(string agentSessionId, string fragment) =>
        new()
        {
            AgentSessionId = agentSessionId,
            ReasoningChunk = new ReasoningChunk { Fragment = fragment, Kind = ReasoningKind.Summary },
        };

    private static Event PhaseChange(string agentSessionId) =>
        new()
        {
            AgentSessionId = agentSessionId,
            ProviderRequestPhaseChanged = new ProviderRequestPhaseChangedEvent { Phase = ProviderRequestPhase.Idle },
        };

    private static string Render(IReadOnlyList<ILiveBufferItem> items, LiveBufferRenderContext context) =>
        string.Join('|', items.SelectMany(item => item.Render(context).Lines).Select(static line => line.Text));

    private static int Count(string value, string fragment) =>
        value.Split(fragment, StringSplitOptions.None).Length - 1;

    private sealed class RawReasoningFixture : IAsyncDisposable
    {
        private readonly LiveBufferRenderContext _liveContext = new(120, new TerminalPalette(false));
        private readonly ScrollbackRenderContext _scrollbackContext;
        private readonly List<string> _drawn = [];
        private readonly List<string> _committed = [];

        public RawReasoningFixture()
        {
            _scrollbackContext = new ScrollbackRenderContext(120, _liveContext.Palette);
            View = new RawActivityView(
                Draw,
                Commit,
                new ToolPresenterRegistry([], new GenericToolPresenter()),
                static (_, _) => Task.CompletedTask);
        }

        public RawActivityView View { get; }

        public string LastDrawn => _drawn[^1];

        public IReadOnlyList<string> Committed => _committed;

        public string CommittedText => string.Join('|', _committed);

        public ValueTask DisposeAsync() => View.DisposeAsync();

        private Task Draw(IReadOnlyList<ILiveBufferItem> items, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _drawn.Add(Render(items, _liveContext));
            return Task.CompletedTask;
        }

        private Task Commit(
            IScrollbackItem item,
            IReadOnlyList<ILiveBufferItem> items,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _committed.Add(string.Join('|', item.Render(_scrollbackContext)));
            _drawn.Add(Render(items, _liveContext));
            return Task.CompletedTask;
        }
    }
}
