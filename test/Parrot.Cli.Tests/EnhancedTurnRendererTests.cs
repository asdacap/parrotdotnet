using System.Text.RegularExpressions;
using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Config;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class EnhancedTurnRendererTests
{
    [Test]
    public async Task Typed_turn_events_render_cumulative_text_and_sanitize_terminal_content(
        CancellationToken cancellationToken)
    {
        var (completed, output, error) = await Render(
            [
                new Event
                {
                    Id = "admitted-before-turn",
                    InputAdmitted = new InputAdmitted { Content = "already visible" },
                },
                new Event { Id = "start", TurnStarted = new TurnStarted { Model = "model" } },
                new Event { Id = "status", StatusInjected = new StatusInjected() },
                new Event
                {
                    Id = "reminder",
                    ActiveWorkReminderInjected = new ActiveWorkReminderInjected(),
                },
                new Event
                {
                    Id = "context-reminder",
                    ContextReminderInjected = new ContextReminderInjected { UsagePercent = 27 },
                },
                new Event
                {
                    Id = "final-provider-request",
                    FinalProviderRequestPromptInjected = new FinalProviderRequestPromptInjected(),
                },
                new Event
                {
                    Id = "tool-availability-restored",
                    ToolAvailabilityRestoredPromptInjected = new ToolAvailabilityRestoredPromptInjected(),
                },
                new Event
                {
                    Id = "skill-loaded",
                    SkillLoaded = new SkillLoadedEvent { Path = "/skills/example\u001b[2J/SKILL.md" },
                },
                new Event
                {
                    Id = "queued",
                    InputAdmitted = new InputAdmitted { Content = "next\u001b[2J\tline" },
                },
                new Event { Id = "thought-1", ReasoningChunk = new ReasoningChunk { Fragment = "think" } },
                new Event
                {
                    Id = "thought-2",
                    ReasoningChunk = new ReasoningChunk { Fragment = "\u001b]0;title\n" },
                },
                new Event { Id = "text-1", TextChunk = new TextChunk { Fragment = "abcdefgh" } },
                new Event { Id = "text-2", TextChunk = new TextChunk { Fragment = "ij" } },
                new Event
                {
                    Id = "tool-chunk",
                    ToolCallChunk = new ToolCallChunk { ToolName = "unrendered" },
                },
                new Event
                {
                    Id = "tool-started",
                    ToolStarted = new ToolStarted { ToolCallId = "call-1", ToolName = "shell\u001b[31m" },
                },
                new Event
                {
                    Id = "tool-finished",
                    ToolFinished = new ToolFinished { ToolCallId = "call-1", ToolName = "shell\u001b[31m" },
                },
                new Event
                {
                    Id = "tool-cancelled",
                    ToolCancelled = new ToolCancelled { ToolCallId = "call-2", ToolName = "read\u001b[2J" },
                },
                new Event
                {
                    Id = "tool-error",
                    ToolError = new ToolError
                    {
                        ToolCallId = "call-3",
                        ToolName = "write\u001b[31m",
                        Message = "denied\u001b[2J",
                    },
                },
                new Event
                {
                    Id = "agent-started",
                    AgentStarted = new AgentStarted { Name = "explorer\u001b[31m" },
                },
                new Event
                {
                    Id = "agent-finished",
                    AgentFinished = new AgentFinished { Name = "explorer\u001b[31m", ElapsedMs = 7_000 },
                },
                new Event
                {
                    Id = "agent-failed",
                    AgentFailed = new AgentFailed { Name = "reviewer\u001b[31m", Message = "boom\u001b[2J" },
                },
                new Event { Id = "compaction-started", CompactionStarted = new CompactionStarted() },
                new Event { Id = "compaction-finished", CompactionFinished = new CompactionFinished() },
                new Event
                {
                    Id = "compaction-failed",
                    CompactionFailed = new CompactionFailed { Message = "boom\u001b[2J" },
                },
                new Event { Id = "text-3", TextChunk = new TextChunk { Fragment = "tail" } },
                new Event
                {
                    Id = "ended",
                    TurnEnded = new TurnEnded { FinishReason = "stop\u001b[2J", InputTokens = 3, OutputTokens = 4 },
                },
            ],
            cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(error).IsEmpty();
        _ = await Assert.That(output).Contains("  input admitted: already visible");
        _ = await Assert.That(output).Contains("  turn started: model");
        _ = await Assert.That(output).Contains("↻ Status prompt injected");
        _ = await Assert.That(output).DoesNotContain("  ↻ Status prompt injected");
        _ = await Assert.That(output).Contains("↻ Active work reminder injected");
        _ = await Assert.That(output).DoesNotContain("  ↻ Active work reminder injected");
        _ = await Assert.That(output).Contains("↻ Context reminder injected (27% context used)");
        _ = await Assert.That(output).DoesNotContain("  ↻ Context reminder injected");
        _ = await Assert.That(output).Contains("↻ Final provider request prompt injected");
        _ = await Assert.That(output).DoesNotContain("  ↻ Final provider request prompt injected");
        _ = await Assert.That(output).Contains("↻ Tool availability restored prompt injected");
        _ = await Assert.That(output).DoesNotContain("  ↻ Tool availability restored prompt injected");
        _ = await Assert.That(output).Contains("↻ Skill loaded: /skills/example[2J/SKILL.md");
        _ = await Assert.That(output).DoesNotContain("  ↻ Skill loaded:");
        _ = await Assert.That(output).Contains("  queued: next[2J    line");
        _ = await Assert.That(output).DoesNotContain("think]0;title");
        _ = await Assert.That(output).Contains("● abcdef\r\n  ghij\r\n");
        _ = await Assert.That(output).Contains("  tool call unrendered:");
        _ = await Assert.That(output).Contains("  * shell[31m started");
        _ = await Assert.That(output).Contains("  + shell[31m finished");
        _ = await Assert.That(output).Contains("  - read[2J cancelled");
        _ = await Assert.That(output).Contains("  ! write[31m: denied[2J");
        _ = await Assert.That(output).Contains("  * agent explorer[31m started");
        _ = await Assert.That(output).Contains("  + agent explorer[31m finished (7s)");
        _ = await Assert.That(output).Contains("  ! agent reviewer[31m: boom[2J");
        _ = await Assert.That(output).Contains("  * compaction started");
        _ = await Assert.That(output).Contains("  + compaction finished");
        _ = await Assert.That(output).Contains("  ! compaction failed: boom[2J");
        _ = await Assert.That(output).Contains("● tail\r\n");
        _ = await Assert.That(output).Contains("  stop[2J - 3 total in / 4 total out");
        _ = await Assert.That(Count(output, "● abcdef\r\n")).IsEqualTo(1);
        _ = await Assert.That(UntrustedEscape(output)).IsFalse();
        _ = await Assert.That(output).DoesNotContain("\u001b[?1049");
    }

    [Test]
    public async Task Summary_reasoning_chunks_are_committed_as_one_block(CancellationToken cancellationToken)
    {
        var (completed, output, error) = await Render(
            [
                new Event { Id = "start", TurnStarted = new TurnStarted { Model = "model" } },
                new Event
                {
                    Id = "summary-1",
                    ReasoningChunk = new ReasoningChunk { Fragment = "# fi", Kind = ReasoningKind.Summary },
                },
                new Event
                {
                    Id = "summary-2",
                    ReasoningChunk = new ReasoningChunk
                    {
                        Fragment = "rst\n- **bold**\u001b[2J",
                        Kind = ReasoningKind.Summary,
                    },
                },
                new Event { Id = "ended", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            ],
            cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(error).IsEmpty();
        _ = await Assert.That(output).Contains("✦ first\r\n  • bold");
        _ = await Assert.That(Count(output, "✦")).IsEqualTo(1);
        _ = await Assert.That(output).DoesNotContain("# first");
        _ = await Assert.That(output).DoesNotContain("**bold**");
        _ = await Assert.That(output).DoesNotContain("\u001b[2J");
    }

    [Test]
    public async Task Split_markdown_chunks_are_promoted_to_scrollback_once(CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        Event[] events =
        [
            new Event { Id = "start", TurnStarted = new TurnStarted { Model = "model" } },
            new Event { Id = "text-1", TextChunk = new TextChunk { Fragment = "# Head" } },
            new Event { Id = "text-2", TextChunk = new TextChunk { Fragment = "ing\n**bo" } },
            new Event { Id = "text-3", TextChunk = new TextChunk { Fragment = "ld**" } },
            new Event { Id = "ended", TurnEnded = new TurnEnded { FinishReason = "stop" } },
        ];
        foreach (var published in events)
        {
            await stream.WriteAsync(published, cancellationToken);
        }

        stream.Complete();
        using var output = new StringWriter();
        using var error = new StringWriter();

        using var driver = new CliLifecycleDriver(enhanced: true);
        var terminal = new TestTerminal(driver.Input, output, error, 80);
        var configuration = new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml"));
        var completed = await new EnhancedTurnRenderer(terminal, configuration, new ToolPresenterRegistry([], new GenericToolPresenter())).RenderTurn(stream.Reader, cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(error.ToString()).IsEmpty();
        _ = await Assert.That(output.ToString()).Contains("Heading\r\n");
        _ = await Assert.That(output.ToString()).Contains("bold\r\n");
        _ = await Assert.That(Count(output.ToString(), "Heading\r\n")).IsEqualTo(1);
        _ = await Assert.That(Count(output.ToString(), "bold\r\n")).IsEqualTo(1);
    }

    [Test]
    [Arguments("main")]
    [Arguments("child")]
    public async Task Session_turn_renders_retry_notices_when_activity_events_are_disabled(
        string retryAgentSessionId,
        CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        foreach (var published in new Event[]
        {
            new() { AgentSessionId = "main", TurnStarted = new TurnStarted { Model = "model" } },
            new()
            {
                AgentSessionId = "child",
                AgentStarted = new AgentStarted { ParentAgentSessionId = "main", Name = "worker" },
            },
            new()
            {
                AgentSessionId = retryAgentSessionId,
                RetryNotice = new RetryNotice
                {
                    Attempt = 2,
                    RetryAfterMs = 2000,
                    Reason = "Provider timeout\u001b[2J",
                },
            },
            new()
            {
                AgentSessionId = retryAgentSessionId,
                PlanValidationRepairInjected = new PlanValidationRepairInjected { Diagnostic = "Invalid plan\u001b[2J" },
            },
            new()
            {
                AgentSessionId = retryAgentSessionId,
                PendingChildQuestionReminderInjected = new PendingChildQuestionReminderInjected(),
            },
            new() { AgentSessionId = "main", TurnEnded = new TurnEnded { FinishReason = "stop" } },
        })
        {
            await stream.WriteAsync(published, cancellationToken);
        }

        stream.Complete();
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var driver = new CliLifecycleDriver(enhanced: true);
        var terminal = new TestTerminal(driver.Input, output, error, 80);
        var configuration = new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml"));
        var committed = new List<string>();
        var context = new ScrollbackRenderContext(80, new TerminalPalette(false), configuration.InlineDiff);
        var completed = await new EnhancedTurnRenderer(
            terminal, configuration, new ToolPresenterRegistry([], new GenericToolPresenter())).RenderSessionTurn(
                stream.Reader,
                static (_, _) => Task.CompletedTask,
                static (_, _) => Task.CompletedTask,
                static (_, _) => Task.CompletedTask,
                (scrollback, _, _) =>
                {
                    committed.AddRange(scrollback.Render(context));
                    return Task.CompletedTask;
                },
                new ForegroundTurn(),
                cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(committed.Count).IsEqualTo(3);
        _ = await Assert.That(committed[0]).Contains("retry 2 in 2000 ms: Provider timeout[2J");
        _ = await Assert.That(committed[0]).DoesNotContain("\u001b[2J");
        _ = await Assert.That(committed[1]).Contains("Retrying after plan validation failure: Invalid plan[2J");
        _ = await Assert.That(committed[1]).DoesNotContain("\u001b[2J");
        _ = await Assert.That(committed[2]).Contains("Retrying with pending child question reminder");
    }

    [Test]
    public async Task Before_render_runs_before_each_event(CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        Event[] events =
        [
            new Event { Id = "none" },
            new Event { Id = "admitted-before-turn", InputAdmitted = new InputAdmitted { Content = "sent" } },
            new Event { Id = "promoted", InputPromoted = new InputPromoted { InputId = "input" } },
            new Event { Id = "retry", RetryNotice = new RetryNotice { Attempt = 1 } },
            new Event { Id = "started", TurnStarted = new TurnStarted { Model = "model" } },
            new Event { Id = "tool-chunk", ToolCallChunk = new ToolCallChunk { ToolName = "shell" } },
            new Event { Id = "first-visible", TextChunk = new TextChunk { Fragment = "answer" } },
            new Event { Id = "later-visible", ToolStarted = new ToolStarted { ToolName = "shell" } },
            new Event { Id = "ended", TurnEnded = new TurnEnded { FinishReason = "stop" } },
        ];
        foreach (var published in events)
        {
            await stream.WriteAsync(published, cancellationToken);
        }

        stream.Complete();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var callbackIds = new List<string>();

        async Task BeforeRender(Event published, CancellationToken token)
        {
            callbackIds.Add(published.Id);
            await output.WriteAsync($"before:{published.Id}|".AsMemory(), token);
        }

        using var driver = new CliLifecycleDriver(enhanced: true);
        var terminal = new TestTerminal(driver.Input, output, error, 80);
        var configuration = new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml"));
        var completed = await new EnhancedTurnRenderer(terminal, configuration, new ToolPresenterRegistry([], new GenericToolPresenter())).RenderTurn(stream.Reader, BeforeRender, cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(string.Join(',', callbackIds))
            .IsEqualTo("none,admitted-before-turn,promoted,retry,started,tool-chunk,first-visible,later-visible,ended");
        _ = await Assert.That(output.ToString()).StartsWith("before:none|");
        _ = await Assert.That(output.ToString()).Contains("event none has no payload");
        _ = await Assert.That(output.ToString()).Contains("input admitted: sent");
        _ = await Assert.That(output.ToString()).Contains("input promoted: input");
        _ = await Assert.That(output.ToString()).Contains("retry 1 in 0 ms:");
        _ = await Assert.That(output.ToString()).Contains("turn started: model");
        _ = await Assert.That(output.ToString()).Contains("tool call shell:");
    }

    [Test]
    public async Task Enhanced_turn_ignores_agent_statistics_events(CancellationToken cancellationToken)
    {
        var (completed, output, error) = await Render(
            [
                new Event { AgentSessionId = "main", TurnStarted = new TurnStarted { Model = "model" } },
                new Event
                {
                    AgentSessionId = "main",
                    AgentStatisticsUpdated = new AgentStatisticsUpdatedEvent
                    {
                        InputTokens = 10,
                        CachedInputTokens = 5,
                        OutputTokens = 2,
                        ContextSize = 12,
                        ContextLimit = 100,
                    },
                },
                new Event
                {
                    AgentSessionId = "main",
                    TurnEnded = new TurnEnded { FinishReason = "stop", InputTokens = 10, OutputTokens = 2 },
                },
            ],
            cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(output).DoesNotContain("AgentStatisticsUpdated");
        _ = await Assert.That(output).Contains("stop - 10 total in / 2 total out");
        _ = await Assert.That(error).IsEmpty();
    }

    [Test]
    public async Task Child_turn_completion_does_not_complete_the_foreground_turn(
        CancellationToken cancellationToken)
    {
        var (completed, output, error) = await Render(
            [
                new Event
                {
                    Id = "main-start",
                    AgentSessionId = "main-session",
                    TurnStarted = new TurnStarted { Model = "model" },
                },
                new Event
                {
                    Id = "child-started",
                    AgentSessionId = "child-session",
                    AgentStarted = new AgentStarted
                    {
                        ParentAgentSessionId = "main-session",
                        Name = "explorer",
                    },
                },
                new Event
                {
                    Id = "child-turn",
                    AgentSessionId = "child-session",
                    TurnStarted = new TurnStarted { Model = "model" },
                },
                new Event
                {
                    Id = "child-ended",
                    AgentSessionId = "child-session",
                    TurnEnded = new TurnEnded { FinishReason = "child-stop" },
                },
                new Event
                {
                    Id = "main-text",
                    AgentSessionId = "main-session",
                    TextChunk = new TextChunk { Fragment = "main continues" },
                },
                new Event
                {
                    Id = "main-ended",
                    AgentSessionId = "main-session",
                    TurnEnded = new TurnEnded { FinishReason = "main-stop" },
                },
            ],
            cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(error).IsEmpty();
        var childEnded = output.IndexOf("child-stop", StringComparison.Ordinal);
        var mainContinued = output.IndexOf("● main c", StringComparison.Ordinal);
        var mainEnded = output.IndexOf("main-stop", StringComparison.Ordinal);
        _ = await Assert.That(childEnded).IsEqualTo(-1);
        _ = await Assert.That(mainContinued).IsGreaterThanOrEqualTo(0);
        _ = await Assert.That(mainEnded).IsGreaterThan(mainContinued);
    }

    [Test]
    public async Task Failure_commits_the_live_suffix_and_reports_sanitized_error(
        CancellationToken cancellationToken)
    {
        var (completed, output, error) = await Render(
            [
                new Event { Id = "start", TurnStarted = new TurnStarted { Model = "model" } },
                new Event { Id = "text", TextChunk = new TextChunk { Fragment = "partial" } },
                new Event
                {
                    Id = "failed",
                    TurnFailed = new TurnFailed
                    {
                        Message = "bad\u001b[2J\trequest",
                        ProviderResponseBody = "{\"error\":\"broken\u001b[2J\"}\nsecond line",
                    },
                },
            ],
            cancellationToken);

        _ = await Assert.That(completed).IsFalse();
        _ = await Assert.That(output).Contains("● partia\r\n  l\r\n");
        _ = await Assert.That(error).Contains("  bad[2J    request");
        _ = await Assert.That(error).Contains("  provider response:");
        _ = await Assert.That(error).Contains("{\"error\":\"broken[2J\"}\nsecond line");
        _ = await Assert.That(error).DoesNotContain("\u001b[2J");
    }

    [Test]
    public async Task Child_provider_failure_reports_its_response_body(CancellationToken cancellationToken)
    {
        var (completed, _, error) = await Render(
            [
                new Event
                {
                    Id = "main-start",
                    AgentSessionId = "main",
                    TurnStarted = new TurnStarted { Model = "model" },
                },
                new Event
                {
                    Id = "child-agent",
                    AgentSessionId = "child",
                    AgentStarted = new AgentStarted { ParentAgentSessionId = "main", Name = "worker" },
                },
                new Event
                {
                    Id = "child-start",
                    AgentSessionId = "child",
                    TurnStarted = new TurnStarted { Model = "model" },
                },
                new Event
                {
                    Id = "child-failed",
                    AgentSessionId = "child",
                    TurnFailed = new TurnFailed
                    {
                        Message = "child failed",
                        ProviderResponseBody = "{\"child\":true}",
                    },
                },
                new Event
                {
                    Id = "main-ended",
                    AgentSessionId = "main",
                    TurnEnded = new TurnEnded { FinishReason = "stop" },
                },
            ],
            cancellationToken);

        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(error).Contains("  provider response:");
        _ = await Assert.That(error).Contains("{\"child\":true}");
    }

    [Test]
    public async Task Agent_task_progress_snapshots_are_not_committed_by_standalone_turn_rendering(
        CancellationToken cancellationToken)
    {
        var first = new AgentTaskProgressSnapshot
        {
            OriginToolCallId = "call",
            Revision = 1,
            RootNodes =
            {
                new AgentTaskProgressNode
                {
                    Name = "work\u001b[2J\t日本",
                    Status = AgentTaskProgressStatus.Running,
                    Children =
                    {
                        new AgentTaskProgressNode { Name = "pending", Status = AgentTaskProgressStatus.Pending },
                        new AgentTaskProgressNode { Name = "succeeded", Status = AgentTaskProgressStatus.Succeeded },
                        new AgentTaskProgressNode { Name = "failed", Status = AgentTaskProgressStatus.Failed },
                        new AgentTaskProgressNode { Name = "blocked", Status = AgentTaskProgressStatus.Blocked },
                        new AgentTaskProgressNode { Name = "canceled", Status = AgentTaskProgressStatus.Canceled },
                    },
                },
            },
        };
        var second = new AgentTaskProgressSnapshot
        {
            OriginToolCallId = "call",
            Revision = 1,
            RootNodes =
            {
                new AgentTaskProgressNode
                {
                    Name = "work\u001b[2J\t日本",
                    Status = AgentTaskProgressStatus.Succeeded,
                    Children =
                    {
                        new AgentTaskProgressNode { Name = "pending", Status = AgentTaskProgressStatus.Pending },
                        new AgentTaskProgressNode { Name = "succeeded", Status = AgentTaskProgressStatus.Succeeded },
                        new AgentTaskProgressNode { Name = "failed", Status = AgentTaskProgressStatus.Failed },
                        new AgentTaskProgressNode { Name = "blocked", Status = AgentTaskProgressStatus.Blocked },
                        new AgentTaskProgressNode { Name = "canceled", Status = AgentTaskProgressStatus.Canceled },
                    },
                },
            },
        };
        var (completed, output, error) = await Render(
            [
                new Event { AgentTaskProgressSnapshot = first },
                new Event { AgentTaskProgressSnapshot = second },
            ],
            cancellationToken);

        _ = await Assert.That(completed).IsFalse();
        _ = await Assert.That(error).IsEmpty();
        _ = await Assert.That(output).IsEmpty();
    }

    private static async Task<(bool Completed, string Output, string Error)> Render(
        IEnumerable<Event> events,
        CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        foreach (var published in events)
        {
            await stream.WriteAsync(published, cancellationToken);
        }

        stream.Complete();

        using var output = new StringWriter();
        using var error = new StringWriter();
        using var driver = new CliLifecycleDriver(enhanced: true);
        var terminal = new TestTerminal(driver.Input, output, error, 8);
        var configuration = new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml"));
        var completed = await new EnhancedTurnRenderer(terminal, configuration, new ToolPresenterRegistry([], new GenericToolPresenter())).RenderTurn(stream.Reader, cancellationToken);
        return (completed, output.ToString(), error.ToString());
    }

    private static bool UntrustedEscape(string output)
    {
        var withoutOwnedAnsi = Regex.Replace(
            output,
            "\\x1b(?:\\[\\?25[lh]|\\[2K|\\[[0-9]+A|\\[(?:0|2|31|32|36)m)",
            string.Empty,
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        return withoutOwnedAnsi.Contains('\u001b', StringComparison.Ordinal);
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
}
