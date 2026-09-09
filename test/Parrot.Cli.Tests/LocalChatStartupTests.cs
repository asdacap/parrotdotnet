using Grpc.Core;
using Parrot.Protocol;
using Parrot.State;
using Parrot.Store;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class LocalChatStartupTests
{
    [Test]
    [Timeout(15_000)]
    [Arguments("fresh", true, 1, 1)]
    [Arguments("load", false, 1, 0)]
    [Arguments("connect", true, 0, 0)]
    [Arguments("missing", false, 1, 1)]
    [Arguments("rejected", true, 1, 1)]
    [Arguments("inactive-connect", true, 0, 0)]
    [Arguments("race", true, 1, 0)]
    [Arguments("recover", false, 1, 0)]
    [Arguments("recover-race", true, 1, 0)]
    public async Task Startup_selects_existing_sessions_before_configuring_fresh_settings(
        string scenario,
        bool interactivePermissions,
        int expectedLocalOpens,
        int expectedConfigurations,
        CancellationToken cancellationToken)
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        using var workspace = new TestWorkspace();
        var service = new TestService(scenario, workspace);
        await using var localServer = await GrpcServer.StartLocal(service, workspace.LocalSocket, diagnostics.Log, cancellationToken);
        using var localClient = GrpcTransportClient.Connect(TransportAddress.Parse($"unix:{workspace.LocalSocket}"), null, diagnostics.Log);
        await using var activation = scenario == "fresh" ? null : workspace.Admit();
        if (scenario is "load" or "inactive-connect" or "recover-race")
        {
            if (activation is not null)
            {
                await activation.DisposeAsync();
            }
        }

        service.OwnerActivation = activation;
        await using var ownerServer = scenario is "connect" or "inactive-connect" or "rejected" or "race" or "recover" or "recover-race"
            ? await GrpcServer.StartLocal(service, workspace.Resources.SocketPath, diagnostics.Log, cancellationToken)
            : null;
        using var error = new StringWriter();
        var localOpens = 0;
        var configurations = 0;
        using var startup = new LocalChatStartup(
            workspace.Paths,
            workspace.Root,
            "host",
            error,
            diagnostics.Log,
            token =>
            {
                token.ThrowIfCancellationRequested();
                localOpens++;
                return Task.FromResult(localClient.Client);
            },
            (client, token) =>
            {
                token.ThrowIfCancellationRequested();
                configurations++;
                return Task.FromResult(new CreateSessionRequest { Model = "fresh-model", Mode = "fresh-mode" });
            });

        var (openedClient, session) = await startup.Open(interactivePermissions, cancellationToken);

        _ = await Assert.That(localOpens).IsEqualTo(expectedLocalOpens);
        _ = await Assert.That(configurations).IsEqualTo(expectedConfigurations);
        _ = await Assert.That(service.CreateCount).IsEqualTo(expectedConfigurations);
        var expectedResumes = scenario switch
        {
            "fresh" or "connect" or "inactive-connect" => 0,
            _ => 1,
        };
        _ = await Assert.That(service.ResumeCount).IsEqualTo(expectedResumes);
        if (expectedLocalOpens == 1 && scenario is not "race" and not "recover-race")
        {
            _ = await Assert.That(ReferenceEquals(openedClient, localClient.Client)).IsTrue();
        }

        _ = await Assert.That(session.Id).IsEqualTo(expectedConfigurations == 1 ? "new-session" : "existing");
        _ = await Assert.That(session.Model).IsEqualTo(expectedConfigurations == 1 ? "fresh-model" : "stored-model");
        _ = await Assert.That(session.Mode).IsEqualTo(expectedConfigurations == 1 ? "fresh-mode" : "stored-mode");
        if (expectedConfigurations == 1)
        {
            _ = await Assert.That(service.Created?.InteractivePermissions).IsEqualTo(interactivePermissions);
        }

        if (expectedResumes > 0)
        {
            _ = await Assert.That(service.Resumed?.UserSessionId).IsEqualTo("existing");
            _ = await Assert.That(service.Resumed?.WorkingDirectory).IsEqualTo(workspace.Root);
            _ = await Assert.That(service.Resumed?.InteractivePermissions).IsEqualTo(interactivePermissions);
        }

        if (scenario is "connect" or "inactive-connect" or "race" or "rejected" or "recover" or "recover-race")
        {
            _ = await Assert.That(service.Attached?.UserSessionId).IsEqualTo("existing");
            _ = await Assert.That(service.Attached?.WorkingDirectory).IsEqualTo(workspace.Root);
        }

        var expectedAttachments = scenario switch
        {
            "fresh" or "load" or "missing" => 0,
            "race" or "recover-race" or "rejected" => 2,
            _ => 1,
        };
        _ = await Assert.That(service.AttachCount).IsEqualTo(expectedAttachments);

        var log = diagnostics.Read();
        _ = await Assert.That(log).Contains("event=\"local.open.start\"");
        _ = await Assert.That(log).Contains("event=\"local.open.complete\"");
        _ = await Assert.That(log.Contains(workspace.Root, StringComparison.Ordinal)).IsFalse();
        _ = await Assert.That(log.Contains("fresh-model", StringComparison.Ordinal)).IsFalse();
        _ = await Assert.That(log.Contains("Attachment rejected.", StringComparison.Ordinal)).IsFalse();
        var diagnostic = error.ToString();
        if (scenario == "fresh")
        {
            _ = await Assert.That(diagnostic).IsEmpty();
        }
        else
        {
            _ = await Assert.That(diagnostic).Contains(scenario switch
            {
                "load" or "recover" => "loaded existing user session existing",
                "connect" or "inactive-connect" or "race" or "recover-race" => "connected to existing user session existing",
                _ => "unable to connect to existing user session existing",
            });
            _ = await Assert.That(diagnostic.Contains("creating a new user session", StringComparison.Ordinal))
                .IsEqualTo(expectedConfigurations == 1);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Cancellation_does_not_initialize_local_providers_or_create_a_fallback(
        bool duringAttach, CancellationToken cancellationToken)
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        using var workspace = new TestWorkspace();
        await using var activation = workspace.Admit();
        var service = new TestService("cancel", workspace);
        await using var server = await GrpcServer.StartLocal(service, workspace.Resources.SocketPath, diagnostics.Log, cancellationToken);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var error = new StringWriter();
        var localOpens = 0;
        var configurations = 0;
        using var startup = new LocalChatStartup(
            workspace.Paths,
            workspace.Root,
            "host",
            error,
            diagnostics.Log,
            token =>
            {
                localOpens++;
                throw new InvalidOperationException("Local providers must remain lazy.");
            },
            (client, token) =>
            {
                configurations++;
                throw new InvalidOperationException("Fresh settings must remain lazy.");
            });
        if (!duringAttach)
        {
            await stopping.CancelAsync();
        }

        var opening = startup.Open(true, stopping.Token);
        if (duringAttach)
        {
            await service.AttachStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await stopping.CancelAsync();
        }

        _ = await Assert.That(async () => await opening.WaitAsync(cancellationToken)).Throws<OperationCanceledException>();
        _ = await Assert.That(localOpens).IsEqualTo(0);
        _ = await Assert.That(configurations).IsEqualTo(0);
        _ = await Assert.That(service.CreateCount).IsEqualTo(0);
        _ = await Assert.That(service.ResumeCount).IsEqualTo(0);
        _ = await Assert.That(service.AttachStarted.Task.IsCompleted).IsEqualTo(duringAttach);
        _ = await Assert.That(error.ToString()).IsEmpty();
        _ = await Assert.That(diagnostics.Read()).Contains("event=\"local.open.complete\"");
        _ = await Assert.That(diagnostics.Read().Contains(workspace.Root, StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    [Arguments("fail-recovery")]
    [Arguments("cancel-recovery")]
    [Arguments("cancel-retry")]
    public async Task Recovery_failure_or_cancellation_does_not_create_a_fallback(
        string scenario, CancellationToken cancellationToken)
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        using var workspace = new TestWorkspace();
        await using var activation = workspace.Admit();
        var service = new TestService(scenario, workspace);
        await using var localServer = await GrpcServer.StartLocal(service, workspace.LocalSocket, diagnostics.Log, cancellationToken);
        await using var ownerServer = await GrpcServer.StartLocal(service, workspace.Resources.SocketPath, diagnostics.Log, cancellationToken);
        using var localClient = GrpcTransportClient.Connect(TransportAddress.Parse($"unix:{workspace.LocalSocket}"), null, diagnostics.Log);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var error = new StringWriter();
        var localOpens = 0;
        var configurations = 0;
        using var startup = new LocalChatStartup(
            workspace.Paths,
            workspace.Root,
            "host",
            error,
            diagnostics.Log,
            token =>
            {
                token.ThrowIfCancellationRequested();
                localOpens++;
                return Task.FromResult(localClient.Client);
            },
            (client, token) =>
            {
                configurations++;
                throw new InvalidOperationException("Fresh settings must remain lazy.");
            });
        var opening = startup.Open(true, stopping.Token);
        await service.ResumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        if (scenario == "cancel-retry")
        {
            await service.RetryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }

        if (scenario != "fail-recovery")
        {
            await stopping.CancelAsync();
        }

        if (scenario == "cancel-retry")
        {
            _ = await Assert.That(async () => await opening.WaitAsync(cancellationToken)).Throws<OperationCanceledException>();
            _ = await Assert.That(service.AttachCount).IsEqualTo(2);
        }
        else
        {
            var failure = await Assert.That(async () => await opening.WaitAsync(cancellationToken)).Throws<RpcException>();
            _ = await Assert.That(failure?.StatusCode).IsEqualTo(scenario == "cancel-recovery" ? StatusCode.Cancelled : StatusCode.FailedPrecondition);
        }

        _ = await Assert.That(localOpens).IsEqualTo(1);
        _ = await Assert.That(configurations).IsEqualTo(0);
        _ = await Assert.That(service.CreateCount).IsEqualTo(0);
        _ = await Assert.That(service.ResumeCount).IsEqualTo(1);
        _ = await Assert.That(service.Resumed?.UserSessionId).IsEqualTo("existing");
        _ = await Assert.That(service.Resumed?.WorkingDirectory).IsEqualTo(workspace.Root);
        _ = await Assert.That(service.Resumed?.InteractivePermissions).IsTrue();
        _ = await Assert.That(error.ToString()).IsEmpty();
        _ = await Assert.That(diagnostics.Read()).Contains("event=\"local.open.complete\"");
        _ = await Assert.That(diagnostics.Read().Contains(workspace.Root, StringComparison.Ordinal)).IsFalse();
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            Root = Path.Combine("/tmp", "ps", Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(Root);
            Paths = new StatePaths(Path.Combine(Root, "s"), Path.Combine(Root, "c"), Path.Combine(Root, "d"));
            Resources = new UserSessionResources(Paths, UserSessionId.Parse("existing"), ProjectWorkspace.FromLaunchDirectory(Root));
        }

        public string Root { get; }

        public StatePaths Paths { get; }

        public UserSessionResources Resources { get; }

        public string LocalSocket => Path.Combine(Root, "local.sock");

        public SessionActivationLease Admit()
        {
            var admission = new WorkingDirectoryClaim(Paths.State, "host").CreateFresh(Root, Resources.Id);
            var activation = admission.ActivationLease ?? throw new InvalidOperationException("Admission failed.");
            new SessionIndex(Resources).Publish(new SessionMeta
            {
                Id = Resources.Id.Value,
                WorkingDirectory = Root,
                ProviderId = "stored-provider",
                Model = "stored-model",
                Mode = "stored-mode",
            });
            return activation;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class TestService(string scenario, TestWorkspace workspace) : GeneratedParrot.ParrotBase
    {
        public SessionActivationLease? OwnerActivation { get; set; }

        public TaskCompletionSource RetryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ResumeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int AttachCount { get; private set; }

        public int CreateCount { get; private set; }

        public int ResumeCount { get; private set; }

        public CreateSessionRequest? Created { get; private set; }

        public ResumeSessionRequest? Resumed { get; private set; }

        public AttachSessionRequest? Attached { get; private set; }

        public TaskCompletionSource AttachStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<UserSession> CreateSession(CreateSessionRequest request, ServerCallContext context)
        {
            CreateCount++;
            Created = request;
            return Task.FromResult(new UserSession { Id = "new-session", Model = request.Model, Mode = request.Mode });
        }

        public override async Task<UserSession> ResumeSession(ResumeSessionRequest request, ServerCallContext context)
        {
            ResumeCount++;
            Resumed = request;
            _ = ResumeStarted.TrySetResult();
            if (scenario == "cancel-recovery")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            }

            if (scenario == "fail-recovery")
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Stored settings are unavailable."));
            }

            if (scenario is "race" or "recover-race" or "cancel-retry")
            {
                throw new RpcException(new Status(StatusCode.AlreadyExists, "Another runtime owns the session."));
            }

            var admission = new WorkingDirectoryClaim(workspace.Paths.State, "host")
                .Resume(workspace.Root, workspace.Resources.Id);
            await using var resumedActivation = admission.ActivationLease
                ?? throw new RpcException(new Status(StatusCode.AlreadyExists, "Another runtime owns the session."));

            return new UserSession { Id = request.UserSessionId, Model = "stored-model", Mode = "stored-mode", Loaded = true };
        }

        public override async Task<UserSession> AttachSession(AttachSessionRequest request, ServerCallContext context)
        {
            AttachCount++;
            Attached = request;
            _ = AttachStarted.TrySetResult();
            if (scenario == "recover")
            {
                if (OwnerActivation is { } ownerActivation)
                {
                    await ownerActivation.DisposeAsync();
                }

                throw new RpcException(new Status(StatusCode.PermissionDenied, "The owner stopped."));
            }

            if (scenario is "race" or "recover-race" or "cancel-retry" && AttachCount == 1)
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Attachment is not admitted yet."));
            }

            if (scenario == "cancel-retry")
            {
                _ = RetryStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            }

            if (scenario == "cancel")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            }

            if (scenario is "rejected" or "cancel-recovery" or "fail-recovery")
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, "Attachment rejected."));
            }

            return new UserSession { Id = request.UserSessionId, Model = "stored-model", Mode = "stored-mode", Loaded = true };
        }
    }
}
