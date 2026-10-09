using Grpc.Core;
using Parrot.Cli.Commands;
using Parrot.Config;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class TerminalSessionControllerTests
{
    [Test]
    public async Task Successful_loading_commits_route_model_and_mode_and_retains_target_until_disposal(
        CancellationToken cancellationToken)
    {
        var initial = new TestInvoker();
        var selected = new TestInvoker();
        var routing = new TerminalSessionCallInvoker(initial);
        var current = new UserSession { Id = "old", Model = "old-model", Mode = "build" };
        ISlashSession slash = new SlashSession(
            routing.Client,
            current,
            new Configuration(Path.Combine(Path.GetTempPath(), "parrot-controller-config.yaml")),
            true,
            new UnusedBinding());
        using var owner = new CountedOwner();
        using ITerminalSessionNavigation navigation = new TestNavigation(
            new TerminalSessionTarget(
                selected,
                new UserSession { Id = "selected", Model = "selected-model", Mode = "plan" },
                owner));
        using var controller = new TerminalSessionController(
            navigation,
            routing,
            slash,
            current,
            (replacement, client, commit, token) =>
            {
                token.ThrowIfCancellationRequested();
                commit();
                return Task.CompletedTask;
            });

        await controller.Load("selected", cancellationToken);
        await slash.Compact(null, cancellationToken);

        _ = await Assert.That(slash.Id).IsEqualTo("selected");
        _ = await Assert.That(slash.Model).IsEqualTo("selected-model");
        _ = await Assert.That(slash.Mode).IsEqualTo("plan");
        _ = await Assert.That(routing.Target).IsEqualTo(selected);
        _ = await Assert.That(selected.CompactedId).IsEqualTo("selected");
        _ = await Assert.That(initial.Calls).IsEqualTo(0);
        _ = await Assert.That(selected.Calls).IsEqualTo(1);
        _ = await Assert.That(owner.Count).IsEqualTo(0);
        controller.Dispose();
        controller.Dispose();
        _ = await Assert.That(owner.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Selecting_current_session_does_not_open_or_bind(CancellationToken cancellationToken)
    {
        var invoker = new TestInvoker();
        var routing = new TerminalSessionCallInvoker(invoker);
        var current = new UserSession { Id = "old", Model = "old-model", Mode = "build" };
        ISlashSession slash = new SlashSession(
            routing.Client,
            current,
            new Configuration(Path.Combine(Path.GetTempPath(), "parrot-controller-config.yaml")),
            true,
            new UnusedBinding());
        using var navigation = new TestNavigation();
        using var controller = new TerminalSessionController(
            navigation,
            routing,
            slash,
            current,
            (_, _, _, _) => throw new InvalidOperationException("binding must not run"));

        await controller.Load("old", cancellationToken);

        _ = await Assert.That(navigation.OpenedIds).IsEmpty();
        _ = await Assert.That(routing.Target).IsEqualTo(invoker);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Binding_failure_restores_route_and_current_session_and_disposes_candidate(
        bool afterCommit,
        CancellationToken cancellationToken)
    {
        var initial = new TestInvoker();
        var selected = new TestInvoker();
        var routing = new TerminalSessionCallInvoker(initial);
        var current = new UserSession { Id = "old", Model = "old-model", Mode = "build" };
        ISlashSession slash = new SlashSession(
            routing.Client,
            current,
            new Configuration(Path.Combine(Path.GetTempPath(), "parrot-controller-config.yaml")),
            true,
            new UnusedBinding());
        using var owner = new CountedOwner();
        using var navigation = new TestNavigation(
            new TerminalSessionTarget(
                selected,
                new UserSession { Id = "selected", Model = "selected-model", Mode = "plan" },
                owner));
        using var controller = new TerminalSessionController(
            navigation,
            routing,
            slash,
            current,
            (_, _, commit, _) =>
            {
                if (afterCommit)
                {
                    commit();
                }

                throw new InvalidOperationException("binding failed");
            });

        _ = await Assert.That(async () => await controller.Load("selected", cancellationToken))
            .Throws<InvalidOperationException>();
        await slash.Compact(null, cancellationToken);

        _ = await Assert.That(slash.Id).IsEqualTo("old");
        _ = await Assert.That(slash.Model).IsEqualTo("old-model");
        _ = await Assert.That(slash.Mode).IsEqualTo("build");
        _ = await Assert.That(routing.Target).IsEqualTo(initial);
        _ = await Assert.That(initial.CompactedId).IsEqualTo("old");
        _ = await Assert.That(owner.Count).IsEqualTo(1);
        controller.Dispose();
        _ = await Assert.That(owner.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Cancellation_during_binding_disposes_candidate_and_preserves_current(CancellationToken cancellationToken)
    {
        var initial = new TestInvoker();
        var routing = new TerminalSessionCallInvoker(initial);
        var current = new UserSession { Id = "old", Model = "old-model", Mode = "build" };
        ISlashSession slash = new SlashSession(
            routing.Client,
            current,
            new Configuration(Path.Combine(Path.GetTempPath(), "parrot-controller-config.yaml")),
            true,
            new UnusedBinding());
        using var owner = new CountedOwner();
        using var navigation = new TestNavigation(
            new TerminalSessionTarget(
                new TestInvoker(),
                new UserSession { Id = "selected" },
                owner));
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var controller = new TerminalSessionController(
            navigation,
            routing,
            slash,
            current,
            async (_, _, _, token) =>
            {
                await stopping.CancelAsync();
                token.ThrowIfCancellationRequested();
            });

        _ = await Assert.That(async () => await controller.Load("selected", stopping.Token))
            .Throws<OperationCanceledException>();

        _ = await Assert.That(slash.Id).IsEqualTo("old");
        _ = await Assert.That(routing.Target).IsEqualTo(initial);
        _ = await Assert.That(owner.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Repeated_switches_can_return_to_the_original_host(CancellationToken cancellationToken)
    {
        var initial = new TestInvoker();
        var selected = new TestInvoker();
        var routing = new TerminalSessionCallInvoker(initial);
        var current = new UserSession { Id = "old", Model = "old-model", Mode = "build" };
        ISlashSession slash = new SlashSession(
            routing.Client,
            current,
            new Configuration(Path.Combine(Path.GetTempPath(), "parrot-controller-config.yaml")),
            true,
            new UnusedBinding());
        using var firstOwner = new CountedOwner();
        using var secondOwner = new CountedOwner();
        using var navigation = new TestNavigation(
            new TerminalSessionTarget(
                selected,
                new UserSession { Id = "selected" },
                firstOwner),
            new TerminalSessionTarget(
                initial,
                current,
                secondOwner));
        using var controller = new TerminalSessionController(
            navigation,
            routing,
            slash,
            current,
            (_, _, commit, _) =>
            {
                commit();
                return Task.CompletedTask;
            });

        await controller.Load("selected", cancellationToken);
        await slash.Compact(null, cancellationToken);
        await controller.Load("old", cancellationToken);
        await slash.Compact(null, cancellationToken);
        _ = await controller.List(cancellationToken);

        _ = await Assert.That(selected.CompactedId).IsEqualTo("selected");
        _ = await Assert.That(initial.CompactedId).IsEqualTo("old");
        _ = await Assert.That(navigation.ListedId).IsEqualTo("old");
        _ = await Assert.That(navigation.OpenedIds).IsEquivalentTo(["selected", "old"]);
        _ = await Assert.That(firstOwner.Count + secondOwner.Count).IsEqualTo(0);
        controller.Dispose();
        _ = await Assert.That(firstOwner.Count).IsEqualTo(1);
        _ = await Assert.That(secondOwner.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Prepared_stream_buffers_first_event_and_replays_it_before_the_next(CancellationToken cancellationToken)
    {
        var invoker = new TestInvoker();
        await invoker.Events.WriteAsync(new Event { AgentSessionId = "first" }, cancellationToken);
        await invoker.Events.WriteAsync(new Event { AgentSessionId = "second" }, cancellationToken);
        await using var prepared = await PreparedSessionStream.Open(
            new GeneratedParrot.ParrotClient(invoker),
            "selected",
            cancellationToken,
            cancellationToken);

        _ = await Assert.That(invoker.Listening?.Replay).IsTrue();
        _ = await Assert.That(invoker.Listening?.UserSessionId).IsEqualTo("selected");
        _ = await Assert.That(invoker.Disposals).IsEqualTo(0);
        _ = await Assert.That(await prepared.Call.ResponseStream.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(prepared.Call.ResponseStream.Current.AgentSessionId).IsEqualTo("first");
        _ = await Assert.That(await prepared.Call.ResponseStream.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(prepared.Call.ResponseStream.Current.AgentSessionId).IsEqualTo("second");
    }

    [Test]
    public async Task Deferred_stream_failure_disposes_candidate_call(CancellationToken cancellationToken)
    {
        var invoker = new TestInvoker();
        invoker.Events.Fault(new RpcException(new Status(StatusCode.NotFound, "missing")));

        var failure = await Assert.That(async () => await PreparedSessionStream.Open(
            new GeneratedParrot.ParrotClient(invoker),
            "selected",
            cancellationToken,
            cancellationToken)).Throws<RpcException>();

        _ = await Assert.That(failure?.StatusCode).IsEqualTo(StatusCode.NotFound);
        _ = await Assert.That(invoker.Disposals).IsEqualTo(1);
        _ = await Assert.That(invoker.ListenCancellation.IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task Preparation_cancellation_disposes_candidate_call(CancellationToken cancellationToken)
    {
        var invoker = new TestInvoker();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var preparing = PreparedSessionStream.Open(
            new GeneratedParrot.ParrotClient(invoker),
            "selected",
            cancellationToken,
            stopping.Token);
        await stopping.CancelAsync();

        _ = await Assert.That(async () => await preparing.WaitAsync(CancellationToken.None))
            .Throws<OperationCanceledException>();
        _ = await Assert.That(invoker.Disposals).IsEqualTo(1);
        _ = await Assert.That(invoker.ListenCancellation.IsCancellationRequested).IsTrue();
    }

    private sealed class UnusedBinding : ISlashSessionBinding
    {
        public Task Replace(UserSession session, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CountedOwner : IDisposable
    {
        public int Count { get; private set; }

        public void Dispose() => Count++;
    }

    private sealed class TestNavigation(params TerminalSessionTarget[] targets) : ITerminalSessionNavigation
    {
        private readonly Queue<TerminalSessionTarget> _targets = new(targets);

        public List<string> OpenedIds { get; } = [];

        public string? ListedId { get; private set; }

        public Task<ListSessionsResponse> List(UserSession current, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ListedId = current.Id;
            return Task.FromResult(new ListSessionsResponse());
        }

        public Task<TerminalSessionTarget> Open(UserSession current, string userSessionId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenedIds.Add(userSessionId);
            return Task.FromResult(_targets.Dequeue());
        }

        public void Dispose()
        {
            while (_targets.TryDequeue(out var target))
            {
                target.Dispose();
            }
        }
    }

    private sealed class TestInvoker : CallInvoker
    {
        public int Calls { get; private set; }

        public string? CompactedId { get; private set; }

        public ChannelStreamWriter<Event> Events { get; } = new();

        public ListenRequest? Listening { get; private set; }

        public CancellationToken ListenCancellation { get; private set; }

        public int Disposals { get; private set; }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request)
        {
            Calls++;
            if (request is not CompactRequest compact)
            {
                throw new NotSupportedException("Unexpected RPC, including creation");
            }

            CompactedId = compact.UserSessionId;
            return new AsyncUnaryCall<TResponse>(
                Task.FromResult((TResponse)(object)new CompactResponse()),
                Task.FromResult(new Metadata()),
                static () => Status.DefaultSuccess,
                static () => [],
                static () => { });
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request)
        {
            Listening = request as ListenRequest ?? throw new NotSupportedException();
            ListenCancellation = options.CancellationToken;
            return new AsyncServerStreamingCall<TResponse>(
                (IAsyncStreamReader<TResponse>)Events.Reader,
                Task.FromResult(new Metadata()),
                static () => Status.DefaultSuccess,
                static () => [],
                () =>
                {
                    Disposals++;
                    Events.Complete();
                });
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request) => throw new NotSupportedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options) => throw new NotSupportedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options) => throw new NotSupportedException();
    }
}
