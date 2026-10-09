using Grpc.Core;
using Parrot.Protocol;
using Parrot.State;
using Parrot.Store;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class TerminalSessionNavigationTests
{
    [Test]
    [Timeout(15_000)]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Local_active_socket_attachment_and_ownership_race_preserve_exact_target(
        bool ownershipRace, CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "nav", Guid.NewGuid().ToString("N")[..8]);
        _ = Directory.CreateDirectory(root);
        try
        {
            using var diagnostics = new TransportDiagnosticsFixture();
            var paths = new StatePaths(root, root, root);
            var resources = new UserSessionResources(
                paths,
                UserSessionId.Parse("user-session-selected"),
                ProjectWorkspace.FromLaunchDirectory(root));
            var service = new SocketService(root, ownershipRace);
            await using var server = await GrpcServer.StartLocal(
                service,
                resources.SocketPath,
                diagnostics.Log,
                cancellationToken);
            var invoker = new NavigationInvoker { ResumeFailure = StatusCode.AlreadyExists };
            var localOpens = 0;
            using ITerminalSessionNavigation navigation = new LocalTerminalSessionNavigation(
                paths,
                root,
                diagnostics.Log,
                _ =>
                {
                    localOpens++;
                    return Task.FromResult<CallInvoker>(invoker);
                });
            using var selected = await navigation.Open(new UserSession(), resources.Id.Value, cancellationToken);

            _ = await Assert.That(selected.Session.Id).IsEqualTo(resources.Id.Value);
            _ = await Assert.That(service.AttachCount).IsEqualTo(ownershipRace ? 2 : 1);
            _ = await Assert.That(localOpens).IsEqualTo(ownershipRace ? 1 : 0);
            _ = await Assert.That(service.Attached?.WorkingDirectory).IsEqualTo(root);
            _ = await Assert.That(invoker.Resumed?.UserSessionId).IsEqualTo(ownershipRace ? resources.Id.Value : null);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [Timeout(15_000)]
    public async Task Local_listing_and_missing_socket_resume_take_over_exact_local_session(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "nav", Guid.NewGuid().ToString("N")[..8]);
        _ = Directory.CreateDirectory(root);
        try
        {
            using var diagnostics = new TransportDiagnosticsFixture();
            var invoker = new NavigationInvoker { RequireTakeOver = true };
            using ITerminalSessionNavigation navigation = new LocalTerminalSessionNavigation(
                new StatePaths(root, root, root),
                root,
                diagnostics.Log,
                _ => Task.FromResult<CallInvoker>(invoker));
            var current = new UserSession { WorkingDirectory = "/another/host" };
            _ = await navigation.List(current, cancellationToken);
            using var selected = await navigation.Open(current, "user-session-selected", cancellationToken);

            _ = await Assert.That(invoker.ListedWorkspace).IsEqualTo(root);
            _ = await Assert.That(invoker.Resumed?.WorkingDirectory).IsEqualTo(root);
            _ = await Assert.That(invoker.Resumed?.UserSessionId).IsEqualTo("user-session-selected");
            _ = await Assert.That(selected.Session.Id).IsEqualTo("user-session-selected");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Remote_selection_uses_server_workspace_and_resumes_only_the_selected_id(CancellationToken cancellationToken)
    {
        var invoker = new NavigationInvoker { AttachMissing = true };
        using ITerminalSessionNavigation navigation = new ExplicitRemoteTerminalSessionNavigation(invoker);
        var current = new UserSession { Id = "current", WorkingDirectory = "/server/workspace" };

        _ = await navigation.List(current, cancellationToken);
        using var target = await navigation.Open(current, "selected", cancellationToken);

        _ = await Assert.That(invoker.ListedWorkspace).IsEqualTo(current.WorkingDirectory);
        _ = await Assert.That(invoker.Attached?.WorkingDirectory).IsEqualTo(current.WorkingDirectory);
        _ = await Assert.That(invoker.Resumed?.WorkingDirectory).IsEqualTo(current.WorkingDirectory);
        _ = await Assert.That(invoker.Resumed?.UserSessionId).IsEqualTo("selected");
        _ = await Assert.That(invoker.Resumed?.InteractivePermissions).IsTrue();
        _ = await Assert.That(target.Session.Id).IsEqualTo("selected");
    }

    [Test]
    public async Task Remote_attachment_does_not_resume_an_active_target(CancellationToken cancellationToken)
    {
        var invoker = new NavigationInvoker();
        using ITerminalSessionNavigation navigation = new ExplicitRemoteTerminalSessionNavigation(invoker);
        using var target = await navigation.Open(
            new UserSession { WorkingDirectory = "/server/workspace" }, "selected", cancellationToken);

        _ = await Assert.That(target.Session.Id).IsEqualTo("selected");
        _ = await Assert.That(invoker.Resumed).IsNull();
    }

    [Test]
    public async Task Missing_remote_workspace_prevents_listing_and_loading(CancellationToken cancellationToken)
    {
        var invoker = new NavigationInvoker();
        using ITerminalSessionNavigation navigation = new ExplicitRemoteTerminalSessionNavigation(invoker);
        _ = await Assert.That(async () => await navigation.List(new UserSession(), cancellationToken)).Throws<InvalidOperationException>();
        _ = await Assert.That(async () => await navigation.Open(new UserSession(), "selected", cancellationToken)).Throws<InvalidOperationException>();
        _ = await Assert.That(invoker.ListedWorkspace).IsNull();
        _ = await Assert.That(invoker.Attached).IsNull();
        _ = await Assert.That(invoker.Resumed).IsNull();
    }

    [Test]
    public async Task Remote_semantic_failure_does_not_fall_back_to_resume(CancellationToken cancellationToken)
    {
        var invoker = new NavigationInvoker { AttachFailure = StatusCode.FailedPrecondition };
        using ITerminalSessionNavigation navigation = new ExplicitRemoteTerminalSessionNavigation(invoker);
        var failure = await Assert.That(async () => await navigation.Open(
            new UserSession { WorkingDirectory = "/server/workspace" }, "selected", cancellationToken)).Throws<RpcException>();

        _ = await Assert.That(failure?.StatusCode).IsEqualTo(StatusCode.FailedPrecondition);
        _ = await Assert.That(invoker.Resumed).IsNull();
    }

    [Test]
    public async Task Remote_different_returned_session_is_rejected(CancellationToken cancellationToken)
    {
        var invoker = new NavigationInvoker { ReturnedId = "different" };
        using ITerminalSessionNavigation navigation = new ExplicitRemoteTerminalSessionNavigation(invoker);
        _ = await Assert.That(async () => await navigation.Open(
            new UserSession { WorkingDirectory = "/server/workspace" }, "selected", cancellationToken)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Stable_client_sends_new_calls_to_new_target_and_keeps_open_stream_on_old_target(CancellationToken cancellationToken)
    {
        var first = new NavigationInvoker();
        var second = new NavigationInvoker();
        var invoker = new TerminalSessionCallInvoker(first);
        using var oldStream = invoker.Client.Listen(new ListenRequest { UserSessionId = "old" }, cancellationToken: cancellationToken);
        invoker.Target = second;
        _ = await invoker.Client.ListSessionsAsync(new ListSessionsRequest { WorkingDirectory = "new" }, cancellationToken: cancellationToken);
        using var newStream = invoker.Client.Listen(new ListenRequest { UserSessionId = "new" }, cancellationToken: cancellationToken);
        await first.Events.WriteAsync(new Event { AgentSessionId = "first" }, cancellationToken);
        await second.Events.WriteAsync(new Event { AgentSessionId = "second" }, cancellationToken);

        _ = await Assert.That(await oldStream.ResponseStream.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(oldStream.ResponseStream.Current.AgentSessionId).IsEqualTo("first");
        _ = await Assert.That(await newStream.ResponseStream.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(newStream.ResponseStream.Current.AgentSessionId).IsEqualTo("second");
        _ = await Assert.That(first.ListedWorkspace).IsNull();
        _ = await Assert.That(second.ListedWorkspace).IsEqualTo("new");
    }

    [Test]
    public async Task Every_forwarded_call_shape_captures_its_target(CancellationToken cancellationToken)
    {
        var first = new ShapeInvoker("first");
        var second = new ShapeInvoker("second");
        var forwarding = new TerminalSessionCallInvoker(first);
        var marshaller = Marshallers.StringMarshaller;
        var blocking = new Method<string, string>(MethodType.Unary, "test", "blocking", marshaller, marshaller);
        var upload = new Method<string, string>(MethodType.ClientStreaming, "test", "upload", marshaller, marshaller);
        var duplex = new Method<string, string>(MethodType.DuplexStreaming, "test", "duplex", marshaller, marshaller);
        var options = new CallOptions(cancellationToken: cancellationToken);
        var oldBlocking = forwarding.BlockingUnaryCall(blocking, null, options, "request");
        using var oldUpload = forwarding.AsyncClientStreamingCall(upload, null, options);
        using var oldDuplex = forwarding.AsyncDuplexStreamingCall(duplex, null, options);
        forwarding.Target = second;
        using var newUpload = forwarding.AsyncClientStreamingCall(upload, null, options);
        using var newDuplex = forwarding.AsyncDuplexStreamingCall(duplex, null, options);

        _ = await Assert.That(oldBlocking).IsEqualTo("first");
        _ = await Assert.That(forwarding.BlockingUnaryCall(blocking, null, options, "request")).IsEqualTo("second");
        _ = await Assert.That(await oldUpload.ResponseAsync).IsEqualTo("first");
        _ = await Assert.That(await newUpload.ResponseAsync).IsEqualTo("second");
        _ = await Assert.That(await oldDuplex.ResponseStream.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(oldDuplex.ResponseStream.Current).IsEqualTo("first");
        _ = await Assert.That(await newDuplex.ResponseStream.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(newDuplex.ResponseStream.Current).IsEqualTo("second");
    }

    [Test]
    public async Task Target_disposes_only_its_owned_connection_once()
    {
        using var owner = new CountedOwner();
        var target = new TerminalSessionTarget(new NavigationInvoker(), new UserSession(), owner);
        target.Dispose();
        target.Dispose();
        _ = await Assert.That(owner.Count).IsEqualTo(1);
    }

    private sealed class ShapeInvoker(string identity) : CallInvoker
    {
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request) => (TResponse)(object)identity;

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options) =>
            new(
                new CapturedWriter<TRequest>(),
                Task.FromResult((TResponse)(object)identity),
                Task.FromResult(new Metadata()),
                static () => Status.DefaultSuccess,
                static () => [],
                static () => { });

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options)
        {
            var responses = new ChannelStreamWriter<TResponse>();
            _ = responses.TryWrite((TResponse)(object)identity);
            return new AsyncDuplexStreamingCall<TRequest, TResponse>(
                new CapturedWriter<TRequest>(),
                responses.Reader,
                Task.FromResult(new Metadata()),
                static () => Status.DefaultSuccess,
                static () => [],
                responses.Complete);
        }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request) => throw new NotImplementedException();

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request) => throw new NotImplementedException();
    }

    private sealed class CapturedWriter<T> : IClientStreamWriter<T>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message) => Task.CompletedTask;

        public Task CompleteAsync() => Task.CompletedTask;
    }

    private sealed class SocketService(string workspace, bool ownershipRace) : GeneratedParrot.ParrotBase
    {
        public int AttachCount { get; private set; }

        public AttachSessionRequest? Attached { get; private set; }

        public override Task<UserSession> AttachSession(AttachSessionRequest request, ServerCallContext context)
        {
            AttachCount++;
            Attached = request;
            if (ownershipRace && AttachCount == 1)
            {
                throw new RpcException(new Status(StatusCode.DeadlineExceeded, "ownership changed"));
            }

            return Task.FromResult(new UserSession { Id = request.UserSessionId, WorkingDirectory = workspace });
        }
    }

    private sealed class CountedOwner : IDisposable
    {
        public int Count { get; private set; }

        public void Dispose() => Count++;
    }

    private sealed class NavigationInvoker : CallInvoker
    {
        public bool AttachMissing { get; init; }

        public StatusCode? AttachFailure { get; init; }

        public StatusCode? ResumeFailure { get; init; }

        public bool RequireTakeOver { get; init; }

        public string? ReturnedId { get; init; }

        public string? ListedWorkspace { get; private set; }

        public AttachSessionRequest? Attached { get; private set; }

        public ResumeSessionRequest? Resumed { get; private set; }

        public ChannelStreamWriter<Event> Events { get; } = new();

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request)
        {
            object response;
            switch (request)
            {
                case ListSessionsRequest listed:
                    ListedWorkspace = listed.WorkingDirectory;
                    response = new ListSessionsResponse();
                    break;
                case AttachSessionRequest attached:
                    Attached = attached;
                    if (AttachMissing || AttachFailure is not null)
                    {
                        throw new RpcException(new Status(AttachFailure ?? StatusCode.NotFound, "attachment failed"));
                    }

                    response = new UserSession { Id = ReturnedId ?? attached.UserSessionId, WorkingDirectory = attached.WorkingDirectory };
                    break;
                case ResumeSessionRequest resumed:
                    Resumed = resumed;
                    if (RequireTakeOver && !resumed.TakeOver)
                    {
                        throw new RpcException(new Status(StatusCode.AlreadyExists, "unverifiable owner requires takeover"));
                    }

                    if (ResumeFailure is { } resumeFailure)
                    {
                        throw new RpcException(new Status(resumeFailure, "resume failed"));
                    }

                    response = new UserSession { Id = ReturnedId ?? resumed.UserSessionId, WorkingDirectory = resumed.WorkingDirectory };
                    break;
                default:
                    throw new NotImplementedException();
            }

            return new AsyncUnaryCall<TResponse>(
                Task.FromResult((TResponse)response),
                Task.FromResult(new Metadata()),
                static () => Status.DefaultSuccess,
                static () => [],
                static () => { });
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request) =>
            new(
                (IAsyncStreamReader<TResponse>)Events.Reader,
                Task.FromResult(new Metadata()),
                static () => Status.DefaultSuccess,
                static () => [],
                Events.Complete);

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request) => throw new NotImplementedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotImplementedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotImplementedException();
    }
}
