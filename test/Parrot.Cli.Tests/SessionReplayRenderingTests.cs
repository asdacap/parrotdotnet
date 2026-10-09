using Grpc.Core;
using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class SessionReplayRenderingTests
{
    [Test]
    public async Task Prepared_history_ends_replay_at_first_usage_snapshot_and_never_reenters(CancellationToken cancellationToken)
    {
        var invoker = new StreamInvoker([
            new Event { TurnStarted = new TurnStarted() },
            new Event { QueueSnapshot = new QueueSnapshot() },
            new Event { SessionUsageSnapshot = new SessionUsageSnapshot() },
            new Event { TurnStarted = new TurnStarted() },
            new Event { SessionUsageSnapshot = new SessionUsageSnapshot() },
        ]);
        await using var prepared = await PreparedSessionStream.Open(
            new GeneratedParrot.ParrotClient(invoker), "selected", cancellationToken, cancellationToken);

        _ = await Assert.That(prepared.IsReplaying).IsTrue();
        foreach (var replaying in new[] { true, true, false, false, false })
        {
            _ = await Assert.That(await prepared.Call.ResponseStream.MoveNext(cancellationToken)).IsTrue();
            _ = await Assert.That(prepared.IsReplaying).IsEqualTo(replaying);
        }

        _ = await Assert.That(await prepared.Call.ResponseStream.MoveNext(cancellationToken)).IsFalse();
        _ = await Assert.That(invoker.Listening?.Replay).IsTrue();
    }

    [Test]
    public async Task Live_stream_does_not_enter_replay(CancellationToken cancellationToken)
    {
        var invoker = new StreamInvoker([new Event { TurnStarted = new TurnStarted() }]);
        await using var live = PreparedSessionStream.OpenLive(new GeneratedParrot.ParrotClient(invoker), "selected", cancellationToken);
        _ = await Assert.That(await live.Call.ResponseStream.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(live.IsReplaying).IsFalse();
        _ = await Assert.That(invoker.Listening?.Replay).IsFalse();
    }

    [Test]
    [Arguments(Event.PayloadOneofCase.TurnStarted, true)]
    [Arguments(Event.PayloadOneofCase.TurnEnded, false)]
    [Arguments(Event.PayloadOneofCase.ModeTurnCompleted, false)]
    [Arguments(Event.PayloadOneofCase.TurnFailed, false)]
    public async Task Replay_completion_infers_only_latest_root_lifecycle_once(
        Event.PayloadOneofCase tail,
        bool expectedBusy,
        CancellationToken cancellationToken)
    {
        List<Event> events =
        [
            new() { AgentSessionId = "child", AgentStarted = new AgentStarted { ParentAgentSessionId = "root" } },
            new() { AgentSessionId = "child", TurnStarted = new TurnStarted() },
            new() { AgentSessionId = "root", TurnStarted = new TurnStarted() },
            new() { AgentSessionId = "root", ModeTurnCompleted = new ModeTurnCompleted() },
            new() { AgentSessionId = "root", TurnStarted = new TurnStarted() },
        ];
        events.Add(tail switch
        {
            Event.PayloadOneofCase.TurnStarted => new Event { AgentSessionId = "root", TurnStarted = new TurnStarted() },
            Event.PayloadOneofCase.TurnEnded => new Event { AgentSessionId = "root", TurnEnded = new TurnEnded() },
            Event.PayloadOneofCase.ModeTurnCompleted => new Event { AgentSessionId = "root", ModeTurnCompleted = new ModeTurnCompleted() },
            Event.PayloadOneofCase.TurnFailed => new Event { AgentSessionId = "root", TurnFailed = new TurnFailed() },
            _ => throw new InvalidOperationException(),
        });
        events.Add(new Event { AgentSessionId = "child", TurnStarted = new TurnStarted() });
        events.Add(new Event { AgentSessionId = "child", TurnFailed = new TurnFailed() });
        events.Add(new Event { SessionUsageSnapshot = new SessionUsageSnapshot() });
        events.Add(new Event { AgentSessionId = "root", TurnStarted = new TurnStarted() });
        events.Add(new Event { AgentSessionId = "root", TurnEnded = new TurnEnded() });
        events.Add(new Event { SessionUsageSnapshot = new SessionUsageSnapshot() });
        var invoker = new StreamInvoker(events);
        await using var prepared = await PreparedSessionStream.Open(
            new GeneratedParrot.ParrotClient(invoker), "selected", cancellationToken, cancellationToken);
        var states = new List<bool>();
        prepared.SetReplayCompletion(states.Add);

        while (await prepared.Call.ResponseStream.MoveNext(cancellationToken))
        {
            if (prepared.IsReplaying)
            {
                _ = await Assert.That(states).IsEmpty();
            }
        }

        _ = await Assert.That(states).IsEquivalentTo([expectedBusy]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Basic_replay_shows_old_plan_and_later_history_and_keeps_live_terminal_behavior(bool failed, CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var invoker = new StreamInvoker(HistoryAndLiveTurn(failed));
        await using var prepared = await PreparedSessionStream.Open(
            new GeneratedParrot.ParrotClient(invoker), "selected", cancellationToken, cancellationToken);
        var livePlans = 0;
        var liveStarts = 0;
        var completed = await BasicCli.RenderReplayTurn(
            prepared.Call.ResponseStream,
            output,
            error,
            (published, _) =>
            {
                if (!prepared.IsReplaying)
                {
                    livePlans += published.PayloadCase == Event.PayloadOneofCase.PlanCompleted ? 1 : 0;
                    liveStarts += published.PayloadCase == Event.PayloadOneofCase.TurnStarted ? 1 : 0;
                }

                return Task.CompletedTask;
            },
            () => prepared.IsReplaying,
            cancellationToken);

        _ = await Assert.That(completed).IsEqualTo(!failed);
        _ = await Assert.That(output.ToString()).Contains("old plan report").And.Contains("history after failed turn");
        _ = await Assert.That(output.ToString()).DoesNotContain("old plan dialog");
        _ = await Assert.That(error.ToString()).Contains("old failure");
        _ = await Assert.That(livePlans).IsEqualTo(1);
        _ = await Assert.That(liveStarts).IsEqualTo(1);
        if (failed)
        {
            _ = await Assert.That(error.ToString()).Contains("live failure");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Enhanced_replay_displays_history_without_old_actions_and_preserves_live_actions(bool failed, CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var input = new ScriptedInput();
        var terminal = new TestTerminal(input, output, error, 160);
        var invoker = new StreamInvoker(HistoryAndLiveTurn(failed));
        await using var prepared = await PreparedSessionStream.Open(
            new GeneratedParrot.ParrotClient(invoker), "selected", cancellationToken, cancellationToken);
        var plans = new List<string>();
        var started = 0;
        var finished = 0;
        var permissions = new List<string>();
        await using var rendering = new EnhancedRenderingSession(
            new EnhancedTurnRenderer(terminal),
            new ToolPresenterRegistry([], new GenericToolPresenter()),
            new TerminalFrameRenderer(output, terminal.GetColumns, terminal.GetRows, new TerminalPalette(false), 10, 12, true),
            new TestSlashSession("model"),
            [new PromptValue("> ", string.Empty, 0)],
            (published, _) =>
            {
                if (published.PayloadCase == Event.PayloadOneofCase.PermissionPending)
                {
                    permissions.Add(published.PermissionPending.Id);
                }

                return Task.CompletedTask;
            },
            _ =>
            {
                started++;
                return Task.CompletedTask;
            },
            _ =>
            {
                finished++;
                return Task.CompletedTask;
            },
            static () => false,
            (plan, _) =>
            {
                plans.Add(plan.Markdown);
                return Task.CompletedTask;
            },
            true);

        var completed = await rendering.RunWithReplay(prepared.Call.ResponseStream, () => prepared.IsReplaying, cancellationToken);

        _ = await Assert.That(completed).IsEqualTo(!failed);
        _ = await Assert.That(output.ToString()).Contains("old plan report").And.Contains("history after failed turn");
        _ = await Assert.That(output.ToString()).DoesNotContain("old plan dialog");
        _ = await Assert.That(error.ToString()).Contains("old failure");
        _ = await Assert.That(plans).IsEquivalentTo(["live plan report"]);
        _ = await Assert.That(permissions).IsEquivalentTo(["live-permission"]);
        _ = await Assert.That(started).IsEqualTo(1);
        _ = await Assert.That(finished).IsEqualTo(1);
        if (failed)
        {
            _ = await Assert.That(error.ToString()).Contains("live failure");
        }
    }

    private static IReadOnlyList<Event> HistoryAndLiveTurn(bool failed) =>
    [
        new Event { AgentSessionId = "root", TurnStarted = new TurnStarted() },
        new Event
        {
            AgentSessionId = "root",
            PlanCompleted = new PlanCompleted { Markdown = "old plan report", Dialog = new TurnCompleteDialog { Prompt = "old plan dialog" } },
        },
        new Event { AgentSessionId = "root", PermissionPending = new PendingPermission { Id = "old-permission" } },
        new Event { AgentSessionId = "root", ModeTurnCompleted = new ModeTurnCompleted() },
        new Event { AgentSessionId = "root", TurnStarted = new TurnStarted() },
        new Event { AgentSessionId = "root", TurnFailed = new TurnFailed { Message = "old failure" } },
        new Event { AgentSessionId = "root", TurnStarted = new TurnStarted() },
        new Event { AgentSessionId = "root", TextChunk = new TextChunk { Fragment = "history after failed turn" } },
        new Event { AgentSessionId = "root", ModeTurnCompleted = new ModeTurnCompleted() },
        new Event { SessionUsageSnapshot = new SessionUsageSnapshot() },
        new Event { AgentSessionId = "root", TurnStarted = new TurnStarted() },
        new Event { AgentSessionId = "root", PermissionPending = new PendingPermission { Id = "live-permission" } },
        new Event { AgentSessionId = "root", PlanCompleted = new PlanCompleted { Markdown = "live plan report" } },
        failed
            ? new Event { AgentSessionId = "root", TurnFailed = new TurnFailed { Message = "live failure" } }
            : new Event { AgentSessionId = "root", ModeTurnCompleted = new ModeTurnCompleted() },
    ];

    private sealed class StreamInvoker(IReadOnlyList<Event> events) : CallInvoker
    {
        public ListenRequest? Listening { get; private set; }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            Listening = request as ListenRequest ?? throw new NotSupportedException();
            return new AsyncServerStreamingCall<TResponse>(
                (IAsyncStreamReader<TResponse>)(object)new EventReader(events),
                Task.FromResult(new Metadata()),
                static () => Status.DefaultSuccess,
                static () => [],
                static () => { });
        }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
    }

    private sealed class EventReader(IReadOnlyList<Event> events) : IAsyncStreamReader<Event>
    {
        private int _index = -1;

        public Event Current => events[_index];

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _index++;
            return Task.FromResult(_index < events.Count);
        }
    }
}
