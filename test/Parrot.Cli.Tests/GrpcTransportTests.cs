using System.Net.Sockets;
using Grpc.Core;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class GrpcTransportTests
{
    private const UnixFileMode UserOnlyDirectory = UnixFileMode.UserRead
        | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode UserOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    [Test]
    public async Task Unix_transport_is_owner_only_and_reachable(CancellationToken cancellationToken)
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = TemporaryRoot();
        var path = Path.Combine(root, "control", "parrot.sock");

        try
        {
            await using var server = await GrpcServer.Start(
                new TestService(), TransportAddress.Parse($"unix:{path}"), null, diagnostics.Log, cancellationToken);
            using var client = GrpcTransportClient.Connect(TransportAddress.Parse($"unix:{path}"), null, diagnostics.Log);

            _ = await client.Client.ListModesAsync(new ListModesRequest(), cancellationToken: cancellationToken);

            var directory = Path.GetDirectoryName(path);
            _ = await Assert.That(directory).IsNotNull();
            if (directory is null)
            {
                return;
            }

            _ = await Assert.That(File.GetUnixFileMode(directory)).IsEqualTo(UserOnlyDirectory);
            _ = await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(UserOnlyFile);
        }
        finally
        {
            Delete(root);
        }
    }

    [Test]
    public async Task Active_Unix_transport_is_not_replaced(CancellationToken cancellationToken)
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        var root = TemporaryRoot();
        var path = Path.Combine(root, "control", "parrot.sock");
        try
        {
            await using var first = await GrpcServer.Start(
                new TestService(), TransportAddress.Parse($"unix:{path}"), null, diagnostics.Log, cancellationToken);

            var failure = await Assert.That(async () => await GrpcServer.Start(
                    new TestService(), TransportAddress.Parse($"unix:{path}"), null, diagnostics.Log, cancellationToken))
                .Throws<TransportSocketActiveException>();
            _ = await Assert.That(failure).IsNotNull();
            _ = await Assert.That(failure?.Message ?? string.Empty).Contains("active");

            using var client = GrpcTransportClient.Connect(TransportAddress.Parse($"unix:{path}"), null, diagnostics.Log);
            _ = await client.Client.ListModesAsync(new ListModesRequest(), cancellationToken: cancellationToken);
        }
        finally
        {
            Delete(root);
        }
    }

    [Test]
    public async Task Stale_Unix_transport_is_removed_explicitly(CancellationToken cancellationToken)
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        var root = TemporaryRoot();
        var path = Path.Combine(root, "control", "parrot.sock");
        var directory = Path.GetDirectoryName(path);
        _ = await Assert.That(directory).IsNotNull();
        if (directory is null)
        {
            return;
        }

        _ = Directory.CreateDirectory(directory);
        using (var stale = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            stale.Bind(new UnixDomainSocketEndPoint(path));
        }

        try
        {
            await using var server = await GrpcServer.Start(
                new TestService(), TransportAddress.Parse($"unix:{path}"), null, diagnostics.Log, cancellationToken);
            using var client = GrpcTransportClient.Connect(TransportAddress.Parse($"unix:{path}"), null, diagnostics.Log);
            _ = await client.Client.ListModesAsync(new ListModesRequest(), cancellationToken: cancellationToken);
        }
        finally
        {
            Delete(root);
        }
    }

    [Test]
    public async Task Tcp_requires_the_exact_bearer_token(CancellationToken cancellationToken)
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        var token = TransportToken.Generate();
        await using var server = await GrpcServer.Start(
            new TestService(), TransportAddress.Parse("http://127.0.0.1:0"), token, diagnostics.Log, cancellationToken);
        var address = TransportAddress.Parse(server.Addresses.Single());

        using var authorized = GrpcTransportClient.Connect(address, token, diagnostics.Log);
        _ = await authorized.Client.ListModesAsync(new ListModesRequest(), cancellationToken: cancellationToken);

        using var wrong = GrpcTransportClient.Connect(address, TransportToken.Generate(), diagnostics.Log);
        var failure = await Assert.That(async () => await wrong.Client.ListModesAsync(
                new ListModesRequest(), cancellationToken: cancellationToken))
            .Throws<RpcException>();
        _ = await Assert.That(failure).IsNotNull();
        _ = await Assert.That(failure?.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    [Test]
    public async Task Tcp_cannot_start_without_authentication(CancellationToken cancellationToken)
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        var failure = await Assert.That(async () => await GrpcServer.Start(
                new TestService(), TransportAddress.Parse("http://127.0.0.1:0"), null, diagnostics.Log, cancellationToken))
            .Throws<InvalidOperationException>();

        _ = await Assert.That(failure).IsNotNull();
        _ = await Assert.That(failure?.Message ?? string.Empty).Contains("bearer token");
        _ = await Assert.That(() => GrpcTransportClient.Connect(
            TransportAddress.Parse("http://127.0.0.1:0"), null, diagnostics.Log)).Throws<InvalidOperationException>();
        var log = diagnostics.Read();
        _ = await Assert.That(log).Contains("event=\"host.failure\"");
        _ = await Assert.That(log).Contains("event=\"channel.open.complete\"");
        _ = await Assert.That(log).Contains("error=\"invalid_operation\"");
        _ = await Assert.That(log.Contains("127.0.0.1", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task Transport_addresses_are_explicit()
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        _ = await Assert.That(() => TransportAddress.Parse("127.0.0.1:8710")).Throws<InvalidOperationException>();
        _ = await Assert.That(TransportAddress.Parse("unix:/tmp/parrot.sock").Kind)
            .IsEqualTo(TransportAddressKind.Unix);
        _ = await Assert.That(TransportAddress.Parse("http://127.0.0.1:8710").Kind)
            .IsEqualTo(TransportAddressKind.Http);
        _ = await Assert.That(TransportAddress.Parse("https://example.test:8710").Kind)
            .IsEqualTo(TransportAddressKind.Https);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Unix_transport_refuses_non_socket_entries(bool symbolicLink, CancellationToken cancellationToken)
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        var root = TemporaryRoot();
        _ = Directory.CreateDirectory(root);
        var path = Path.Combine(root, "parrot.sock");
        var target = Path.Combine(root, "target");
        await File.WriteAllTextAsync(target, "preserved", cancellationToken);
        if (symbolicLink)
        {
            _ = File.CreateSymbolicLink(path, target);
        }
        else
        {
            await File.WriteAllTextAsync(path, "preserved", cancellationToken);
        }

        try
        {
            _ = await Assert.That(async () => await GrpcServer.StartLocal(new TestService(), path, diagnostics.Log, cancellationToken))
                .Throws<InvalidOperationException>();
            _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken)).IsEqualTo("preserved");
        }
        finally
        {
            Delete(root);
        }
    }

    [Test]
    public async Task Local_transport_attaches_and_client_disposal_preserves_owner(CancellationToken cancellationToken)
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        var root = TemporaryRoot();
        var path = Path.Combine(root, "parrot.sock");
        try
        {
            await using (var server = await GrpcServer.StartLocal(new TestService(), path, diagnostics.Log, cancellationToken))
            {
                using (var client = GrpcTransportClient.Connect(TransportAddress.Parse($"unix:{path}"), null, diagnostics.Log))
                {
                    var session = await client.Attach(new AttachSessionRequest { UserSessionId = "existing" }, cancellationToken);
                    _ = await Assert.That(session.Id).IsEqualTo("existing");
                }

                using var another = GrpcTransportClient.Connect(TransportAddress.Parse($"unix:{path}"), null, diagnostics.Log);
                _ = await another.Client.ListModesAsync(new ListModesRequest(), cancellationToken: cancellationToken);
            }

            _ = await Assert.That(File.Exists(path)).IsFalse();
        }
        finally
        {
            Delete(root);
        }
    }

    [Test]
    public async Task Local_owner_shutdown_terminates_active_stream(CancellationToken cancellationToken)
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        var root = TemporaryRoot();
        var path = Path.Combine(root, "parrot.sock");
        try
        {
            var server = await GrpcServer.StartLocal(new TestService(), path, diagnostics.Log, cancellationToken);
            using var client = GrpcTransportClient.Connect(TransportAddress.Parse($"unix:{path}"), null, diagnostics.Log);
            using var stream = client.Client.Listen(new ListenRequest(), cancellationToken: cancellationToken);
            try
            {
                _ = await stream.ResponseStream.MoveNext(cancellationToken);
            }
            finally
            {
                await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }

            _ = await Assert.That(async () => await stream.ResponseStream.MoveNext(cancellationToken)).Throws<RpcException>();
        }
        finally
        {
            Delete(root);
        }
    }

    [Test]
    public async Task Attach_is_bounded_and_preserves_caller_cancellation(CancellationToken cancellationToken)
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        var path = Path.Combine(TemporaryRoot(), "missing.sock");
        using var client = GrpcTransportClient.Connect(TransportAddress.Parse($"unix:{path}"), null, diagnostics.Log);
        _ = await Assert.That(async () => await client.Attach(
            new AttachSessionRequest { UserSessionId = "existing" }, cancellationToken)).Throws<TimeoutException>();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await cancelled.CancelAsync();
        _ = await Assert.That(async () => await client.Attach(
            new AttachSessionRequest { UserSessionId = "existing" }, cancelled.Token)).Throws<OperationCanceledException>();
        var log = diagnostics.Read();
        _ = await Assert.That(log).Contains("error=\"timeout\"");
        _ = await Assert.That(log).Contains("outcome=\"cancelled\"");
        _ = await Assert.That(log).Contains("duration_ms=");
        _ = await Assert.That(log.Contains(path, StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task Attach_retries_readiness_and_rejects_wrong_session(CancellationToken cancellationToken)
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        var root = TemporaryRoot();
        var path = Path.Combine(root, "parrot.sock");
        try
        {
            using var client = GrpcTransportClient.Connect(TransportAddress.Parse($"unix:{path}"), null, diagnostics.Log);
            var attaching = client.Attach(new AttachSessionRequest { UserSessionId = "existing" }, cancellationToken);
            await using var server = await GrpcServer.StartLocal(new TestService(), path, diagnostics.Log, cancellationToken);
            _ = await Assert.That((await attaching).Id).IsEqualTo("existing");
            _ = await Assert.That(async () => await client.Attach(
                new AttachSessionRequest { UserSessionId = "wrong" }, cancellationToken)).Throws<InvalidOperationException>();
        }
        finally
        {
            Delete(root);
        }
    }

    [Test]
    public async Task Local_transport_refuses_long_paths_and_preserves_replaced_entries(CancellationToken cancellationToken)
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        var root = TemporaryRoot();
        var path = Path.Combine(root, "parrot.sock");
        try
        {
            _ = await Assert.That(async () => await GrpcServer.StartLocal(
                new TestService(), Path.Combine(root, new string('x', 150) + ".sock"), diagnostics.Log, cancellationToken))
                .Throws<InvalidOperationException>();
            await using (var server = await GrpcServer.StartLocal(new TestService(), path, diagnostics.Log, cancellationToken))
            {
                File.Delete(path);
                await File.WriteAllTextAsync(path, "replacement", cancellationToken);
            }

            _ = await Assert.That(await File.ReadAllTextAsync(path, cancellationToken)).IsEqualTo("replacement");
        }
        finally
        {
            Delete(root);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Attach_diagnostics_are_global_correlated_and_payload_free(
        bool rejected, CancellationToken cancellationToken)
    {
        using var serverDiagnostics = new TransportDiagnosticsFixture();
        using var clientDiagnostics = new TransportDiagnosticsFixture();
        const string sentinel = "transport-secret-payload";
        var token = TransportToken.Generate();
        string addressText;
        await using (var server = await GrpcServer.Start(
            new DiagnosticTestService(rejected),
            TransportAddress.Parse("http://127.0.0.1:0"),
            token,
            serverDiagnostics.Log,
            cancellationToken))
        {
            addressText = server.Addresses.Single();
            using var client = GrpcTransportClient.Connect(TransportAddress.Parse(addressText), token, clientDiagnostics.Log);
            var request = new AttachSessionRequest { UserSessionId = sentinel, WorkingDirectory = sentinel };
            if (rejected)
            {
                var failure = await Assert.That(async () => await client.Attach(request, cancellationToken)).Throws<RpcException>();
                _ = await Assert.That(failure?.StatusCode).IsEqualTo(StatusCode.PermissionDenied);
            }
            else
            {
                _ = await Assert.That((await client.Attach(request, cancellationToken)).Id).IsEqualTo(sentinel);
            }
        }

        var clientLog = clientDiagnostics.Read();
        var serverLog = serverDiagnostics.Read();
        _ = await Assert.That(clientLog).Contains("event=\"attach.start\"");
        _ = await Assert.That(clientLog).Contains("event=\"attach.complete\"");
        _ = await Assert.That(clientLog).Contains(rejected ? "outcome=\"failure\"" : "outcome=\"success\"");
        _ = await Assert.That(clientLog).Contains("event=\"disconnect.complete\"");
        _ = await Assert.That(clientLog.Contains("host.ready", StringComparison.Ordinal)).IsFalse();
        _ = await Assert.That(serverLog).Contains("event=\"host.ready\"");
        _ = await Assert.That(serverLog).Contains("event=\"host.stop.complete\"");
        _ = await Assert.That(serverLog.Contains("attach.start", StringComparison.Ordinal)).IsFalse();
        var attachLines = clientLog.Split('\n').Where(line => line.Contains("event=\"attach.", StringComparison.Ordinal)).ToArray();
        _ = await Assert.That(attachLines.Length).IsEqualTo(2);
        var correlations = attachLines.Select(line => line.Split(' ').Single(field => field.StartsWith("correlation=", StringComparison.Ordinal)));
        _ = await Assert.That(correlations.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(1);
        _ = await Assert.That(attachLines[1]).Contains("duration_ms=");
        _ = await Assert.That(attachLines[1]).Contains(rejected ? "outcome=\"failure\"" : "outcome=\"success\"");
        if (rejected)
        {
            _ = await Assert.That(attachLines[1]).Contains("error=\"PermissionDenied\"");
        }

        foreach (var secret in new[] { sentinel, token.Bearer, addressText })
        {
            _ = await Assert.That((clientLog + serverLog).Contains(secret, StringComparison.Ordinal)).IsFalse();
        }
    }

    private static string TemporaryRoot() =>
        Path.Combine(
            OperatingSystem.IsMacOS() ? "/tmp" : Path.GetTempPath(),
            "pt",
            Guid.NewGuid().ToString("n", System.Globalization.CultureInfo.InvariantCulture));

    private static void Delete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private sealed class DiagnosticTestService(bool rejected) : GeneratedParrot.ParrotBase
    {
        public override Task<UserSession> AttachSession(AttachSessionRequest request, ServerCallContext context) =>
            rejected
                ? throw new RpcException(new Status(StatusCode.PermissionDenied, "transport-secret-payload"))
                : Task.FromResult(new UserSession { Id = request.UserSessionId, Model = "transport-secret-payload" });
    }

    private sealed class TestService : GeneratedParrot.ParrotBase
    {
        public override async Task Listen(ListenRequest request, IServerStreamWriter<Event> responseStream, ServerCallContext context)
        {
            await responseStream.WriteAsync(new Event(), context.CancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
        }

        public override Task<UserSession> AttachSession(AttachSessionRequest request, ServerCallContext context) =>
            Task.FromResult(new UserSession { Id = "existing" });

        public override Task<ListModesResponse> ListModes(ListModesRequest request, ServerCallContext context) =>
            Task.FromResult(new ListModesResponse());
    }
}
