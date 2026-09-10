using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Config;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class EnhancedRenderingSessionTests
{
    [Test]
    [Arguments(20)]
    [Arguments(80)]
    public async Task Question_countdown_preserves_input_and_clears_without_another_key(
        int columns, CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var driver = new CliLifecycleDriver(enhanced: true);
        var terminal = new TestTerminal(driver.Input, output, error, columns);
        var configuration = new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml"));
        var presenters = new ToolPresenterRegistry([], new GenericToolPresenter());
        await using var session = new EnhancedRenderingSession(
            new EnhancedTurnRenderer(terminal, configuration, presenters),
            presenters,
            new TerminalFrameRenderer(output, terminal.GetColumns, new TerminalPalette(false), 1, 1, true),
            new TestSlashSession("provider/model"),
            [new PromptValue("> ", "typed answer", 4)],
            static (_, _) => Task.CompletedTask,
            static _ => Task.CompletedTask,
            static _ => Task.CompletedTask,
            static () => false,
            static (_, _) => Task.CompletedTask,
            false);
        await session.UpdateQuestionCountdown(61, cancellationToken);
        await session.Refresh(cancellationToken);
        _ = await Assert.That(output.ToString()).Contains("Auto-return in 1:01");
        _ = await Assert.That(output.ToString()).Contains("typed answer");
        await session.UpdateQuestionCountdown(60, cancellationToken);
        await session.Refresh(cancellationToken);
        _ = await Assert.That(output.ToString()).Contains("Auto-return in 1:00");
        var beforeClear = output.GetStringBuilder().Length;
        await session.UpdateQuestionCountdown(null, cancellationToken);
        await session.Refresh(cancellationToken);
        var cleared = output.ToString()[beforeClear..];
        _ = await Assert.That(cleared).DoesNotContain("Auto-return in");
        _ = await Assert.That(cleared).Contains("typed answer");
    }

    [Test]
    public async Task Live_usage_is_modeline_only_aggregates_all_agents_and_resets(
        CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        await stream.WriteAsync(
            new Event
            {
                SessionUsageSnapshot = new SessionUsageSnapshot
                {
                    Revision = 1,
                    InputTokens = 100,
                    CachedInputTokens = 25,
                    OutputTokens = 20,
                    ContextSize = 50,
                    ContextLimit = 1000,
                    InputCost = 0.5,
                    OutputCost = 0.25,
                },
            },
            cancellationToken);
        await stream.WriteAsync(
            new Event
            {
                AgentSessionId = "root",
                ProviderCallUsage = new ProviderCallUsage { InputTokens = 30, OutputTokens = 30 },
            },
            cancellationToken);
        await stream.WriteAsync(
            new Event
            {
                AgentSessionId = "child",
                ProviderCallUsage = new ProviderCallUsage { InputTokens = 30, OutputTokens = 30 },
            },
            cancellationToken);
        await stream.WriteAsync(
            new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await stream.WriteAsync(
            new Event { AgentSessionId = "root", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            cancellationToken);
        stream.Complete();

        using var output = new StringWriter();
        using var error = new StringWriter();
        using var driver = new CliLifecycleDriver(enhanced: true);
        var terminal = new TestTerminal(driver.Input, output, error, 120);
        var configuration = new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml"));
        var presenters = new ToolPresenterRegistry([], new GenericToolPresenter());
        var observed = new List<Event.PayloadOneofCase>();
        var time = new TestTimeProvider();
        var expiry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new EnhancedRenderingSession(
            new EnhancedTurnRenderer(terminal, configuration, presenters),
            presenters,
            new TerminalFrameRenderer(output, terminal.GetColumns, new TerminalPalette(false), 10, 12, true),
            new TestSlashSession("provider/model"),
            [new PromptValue("> ", string.Empty, 0)],
            (published, _) =>
            {
                observed.Add(published.PayloadCase);
                return Task.CompletedTask;
            },
            static _ => Task.CompletedTask,
            static _ => Task.CompletedTask,
            static () => true,
            static (_, _) => Task.CompletedTask,
            true,
            time,
            (_, token) => expiry.Task.WaitAsync(token),
            null);

        _ = await Assert.That(await session.Run(stream.Reader, cancellationToken)).IsTrue();
        await session.Refresh(cancellationToken);
        var liveFrame = output.ToString();
        _ = await Assert.That(liveFrame).Contains("+100i +20o (+25.00% cache)");
        _ = await Assert.That(liveFrame).Contains("50/1k");
        _ = await Assert.That(liveFrame).Contains("2i/s 2o/s");
        _ = await Assert.That(liveFrame).Contains("$0.75");
        _ = await Assert.That(observed).DoesNotContain(Event.PayloadOneofCase.ProviderCallUsage);
        _ = await Assert.That(liveFrame).DoesNotContain("Working:");
        _ = await Assert.That(liveFrame).DoesNotContain("Thinking…");

        var commitAt = output.GetStringBuilder().Length;
        await session.Commit(ImmediateScrollbackValue.Trusted(["committed sentinel"]), cancellationToken);
        var committedOutput = output.ToString()[commitAt..];
        _ = await Assert.That(committedOutput).Contains("committed sentinel");
        _ = await Assert.That(committedOutput.Split("committed sentinel").Length).IsEqualTo(2);
        _ = await Assert.That(committedOutput).DoesNotContain("ProviderCallUsage");
        _ = await Assert.That(committedOutput).DoesNotContain("InputTokens");
        _ = await Assert.That(committedOutput).DoesNotContain("OutputTokens");

        var expiryAt = output.GetStringBuilder().Length;
        time.Advance(TimeSpan.FromSeconds(30));
        _ = expiry.TrySetResult();
        await Task.Yield();
        await session.Refresh(cancellationToken);
        _ = await Assert.That(output.ToString()[expiryAt..]).DoesNotContain("i/s");

        var resetAt = output.GetStringBuilder().Length;
        await session.ResetForSession(cancellationToken);
        await session.Refresh(cancellationToken);
        _ = await Assert.That(output.ToString()[resetAt..]).DoesNotContain("i/s");
    }

    [Test]
    public async Task Root_turn_duration_advances_and_respects_lifecycle_boundaries(
        CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var driver = new CliLifecycleDriver(enhanced: true);
        var terminal = new TestTerminal(driver.Input, output, error, 120);
        var configuration = new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml"));
        var presenters = new ToolPresenterRegistry([new WaitToolPresenter()], new GenericToolPresenter());
        var time = new TestTimeProvider();
        var pending = new TaskCompletionSource<Event.PayloadOneofCase>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new EnhancedRenderingSession(
            new EnhancedTurnRenderer(terminal, configuration, presenters),
            presenters,
            new TerminalFrameRenderer(output, terminal.GetColumns, new TerminalPalette(false), 10, 12, true),
            new TestSlashSession("provider/model"),
            [new PromptValue("> ", string.Empty, 0)],
            (published, _) =>
            {
                pending.SetResult(published.PayloadCase);
                return Task.CompletedTask;
            },
            static _ => Task.CompletedTask,
            static _ => Task.CompletedTask,
            static () => true,
            static (_, _) => Task.CompletedTask,
            false,
            time,
            (_, _) => Task.CompletedTask,
            null);

        var running = session.Run(stream.Reader, cancellationToken);
        await Send(new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } });
        await session.Refresh(cancellationToken);
        _ = await Assert.That(output.ToString()).Contains("agent main (running 0s)");

        time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(5));
        await session.Refresh(cancellationToken);
        _ = await Assert.That(output.ToString()).Contains("agent main (running 1m 05s)");

        await Send(new Event
        {
            AgentSessionId = "root",
            ToolStarted = new ToolStarted { ToolCallId = "wait-call", ToolName = "wait" },
        });
        await session.Refresh(cancellationToken);
        _ = await Assert.That(output.ToString()).Contains("Working: wait (running 1m 05s)");

        time.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        await session.Refresh(cancellationToken);
        _ = await Assert.That(output.ToString()).Contains("Working: wait (running 2h 03m 04s)");

        await Send(new Event
        {
            AgentSessionId = "child",
            AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = "worker" },
        });
        await Send(new Event { AgentSessionId = "child", TurnStarted = new TurnStarted { Model = "model" } });
        await Send(new Event { AgentSessionId = "child", TurnEnded = new TurnEnded { FinishReason = "stop" } });
        await session.Refresh(cancellationToken);
        _ = await Assert.That(output.ToString()).Contains("Working: wait (running 2h 03m 04s)");

        await Send(new Event { AgentSessionId = "root", TurnEnded = new TurnEnded { FinishReason = "stop" } });
        var endedAt = output.GetStringBuilder().Length;
        await session.Refresh(cancellationToken);
        _ = await Assert.That(output.ToString()[endedAt..]).DoesNotContain("running");

        await Send(new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } });
        await session.Refresh(cancellationToken);
        _ = await Assert.That(output.ToString()).Contains("agent main (running 0s)");
        await Send(new Event
        {
            AgentSessionId = "root",
            TurnFailed = new TurnFailed { Message = "failed" },
        });
        var failedAt = output.GetStringBuilder().Length;
        await session.Refresh(cancellationToken);
        _ = await Assert.That(output.ToString()[failedAt..]).DoesNotContain("running");

        await Send(new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } });
        time.Advance(TimeSpan.FromSeconds(12));
        await session.Refresh(cancellationToken);
        _ = await Assert.That(output.ToString()).Contains("agent main (running 12s)");

        await session.ResetForSession(cancellationToken);
        var resetAt = output.GetStringBuilder().Length;
        await session.Refresh(cancellationToken);
        _ = await Assert.That(output.ToString()[resetAt..]).DoesNotContain("running");

        stream.Complete();
        _ = await running;

        async Task Send(Event published)
        {
            pending = new TaskCompletionSource<Event.PayloadOneofCase>(TaskCreationOptions.RunContinuationsAsynchronously);
            await stream.WriteAsync(published, cancellationToken);
            _ = await pending.Task.WaitAsync(cancellationToken);
        }
    }

    [Test]
    public async Task Older_stream_without_live_usage_keeps_rates_out_of_the_modeline(
        CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        await stream.WriteAsync(
            new Event
            {
                SessionUsageSnapshot = new SessionUsageSnapshot
                {
                    Revision = 1,
                    InputTokens = 100,
                    OutputTokens = 20,
                    ContextSize = 50,
                    ContextLimit = 1000,
                },
            },
            cancellationToken);
        await stream.WriteAsync(
            new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await stream.WriteAsync(
            new Event { AgentSessionId = "root", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            cancellationToken);
        stream.Complete();

        using var output = new StringWriter();
        using var error = new StringWriter();
        using var driver = new CliLifecycleDriver(enhanced: true);
        var terminal = new TestTerminal(driver.Input, output, error, 120);
        var configuration = new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml"));
        var presenters = new ToolPresenterRegistry([], new GenericToolPresenter());
        await using var session = new EnhancedRenderingSession(
            new EnhancedTurnRenderer(terminal, configuration, presenters),
            presenters,
            new TerminalFrameRenderer(output, terminal.GetColumns, new TerminalPalette(false), 10, 12, true),
            new TestSlashSession("provider/model"),
            [new PromptValue("> ", string.Empty, 0)],
            static (_, _) => Task.CompletedTask,
            static _ => Task.CompletedTask,
            static _ => Task.CompletedTask,
            static () => true,
            static (_, _) => Task.CompletedTask,
            true);

        _ = await Assert.That(await session.Run(stream.Reader, cancellationToken)).IsTrue();
        await session.Refresh(cancellationToken);

        var frame = output.ToString();
        _ = await Assert.That(frame).Contains("+100i +20o");
        _ = await Assert.That(frame).Contains("50/1k");
        _ = await Assert.That(frame).DoesNotContain("i/s");
        _ = await Assert.That(frame).DoesNotContain("o/s");
    }

    [Test]
    public async Task Frame_copies_owned_input_and_places_modeline_before_prompt(
        CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var driver = new CliLifecycleDriver(enhanced: true);
        var terminal = new TestTerminal(driver.Input, output, error, 80);
        var configuration = new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml"));
        var input = new List<ILiveBufferItem> { new PromptValue("first> ", "draft", 0) };
        var renderer = new TerminalFrameRenderer(output, terminal.GetColumns, new TerminalPalette(false), 10, 12, true);
        var presenters = new ToolPresenterRegistry([], new GenericToolPresenter());
        await using var session = new EnhancedRenderingSession(
            new EnhancedTurnRenderer(terminal, configuration, presenters),
            presenters,
            renderer,
            new TestSlashSession("provider/model"),
            input,
            static (_, _) => Task.CompletedTask,
            static _ => Task.CompletedTask,
            static _ => Task.CompletedTask,
            static () => false,
            static (_, _) => Task.CompletedTask,
            true);

        input[0] = new PromptValue("changed> ", "not owned", 0);
        await session.Refresh(cancellationToken);

        var frame = output.ToString();
        var modeline = frame.IndexOf("mode: build", StringComparison.Ordinal);
        var prompt = frame.IndexOf("first> draft", StringComparison.Ordinal);
        _ = await Assert.That(modeline).IsGreaterThanOrEqualTo(0);
        _ = await Assert.That(prompt).IsGreaterThan(modeline);
        _ = await Assert.That(frame).DoesNotContain("changed>");
    }

    [Test]
    public async Task Run_orders_and_deduplicates_queue_snapshot_and_starts_only_main_turn(
        CancellationToken cancellationToken)
    {
        var stream = new ChannelStreamWriter<Event>();
        await stream.WriteAsync(
            new Event
            {
                QueueSnapshot = new QueueSnapshot
                {
                    Revision = 1,
                    FinalChunk = true,
                    Queues =
                    {
                        new QueueState { Name = "zeta", Description = "last queue", ItemCount = 1 },
                        new QueueState { Name = "alpha", Description = "stale queue", ItemCount = 2 },
                        new QueueState { Name = "alpha", Description = "first queue", ItemCount = 3 },
                        new QueueState { Name = "empty", Description = "hidden queue", ItemCount = 0 },
                    },
                },
            },
            cancellationToken);
        await stream.WriteAsync(
            new Event { AgentSessionId = "root", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await stream.WriteAsync(
            new Event
            {
                AgentSessionId = "child",
                AgentStarted = new AgentStarted { ParentAgentSessionId = "root", Name = "worker" },
            },
            cancellationToken);
        await stream.WriteAsync(
            new Event { AgentSessionId = "child", TurnStarted = new TurnStarted { Model = "model" } },
            cancellationToken);
        await stream.WriteAsync(
            new Event { AgentSessionId = "root", TurnEnded = new TurnEnded { FinishReason = "stop" } },
            cancellationToken);
        stream.Complete();

        using var output = new StringWriter();
        using var error = new StringWriter();
        using var driver = new CliLifecycleDriver(enhanced: true);
        var terminal = new TestTerminal(driver.Input, output, error, 100);
        var configuration = new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml"));
        var mainTurns = 0;
        var renderer = new TerminalFrameRenderer(output, terminal.GetColumns, new TerminalPalette(false), 10, 12, true);
        var presenters = new ToolPresenterRegistry([], new GenericToolPresenter());
        await using var session = new EnhancedRenderingSession(
            new EnhancedTurnRenderer(terminal, configuration, presenters),
            presenters,
            renderer,
            new TestSlashSession("provider/model"),
            [new PromptValue("> ", string.Empty, 0)],
            static (_, _) => Task.CompletedTask,
            _ =>
            {
                mainTurns++;
                return Task.CompletedTask;
            },
            static _ => Task.CompletedTask,
            static () => true,
            static (_, _) => Task.CompletedTask,
            true);

        var completed = await session.Run(stream.Reader, cancellationToken);
        await session.Refresh(cancellationToken);

        var frame = output.ToString();
        var alpha = frame.LastIndexOf("queue: alpha · 3 items — first queue", StringComparison.Ordinal);
        var zeta = frame.LastIndexOf("queue: zeta · 1 item — last queue", StringComparison.Ordinal);
        _ = await Assert.That(completed).IsTrue();
        _ = await Assert.That(mainTurns).IsEqualTo(1);
        _ = await Assert.That(alpha).IsGreaterThanOrEqualTo(0);
        _ = await Assert.That(zeta).IsGreaterThan(alpha);
        _ = await Assert.That(frame).DoesNotContain("stale queue");
        _ = await Assert.That(frame).DoesNotContain("hidden queue");
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
