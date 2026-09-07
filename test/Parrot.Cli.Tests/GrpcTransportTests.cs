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
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = TemporaryRoot();
        var path = Path.Combine(root, "control", "parrot.sock");

        try
        {
            await using var server = await GrpcServer.Start(
                new TestService(), TransportAddress.Parse($"unix:{path}"), null, cancellationToken);
            using var client = GrpcTransportClient.Connect(TransportAddress.Parse($"unix:{path}"), null);

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
        var root = TemporaryRoot();
        var path = Path.Combine(root, "control", "parrot.sock");
        try
        {
            await using var first = await GrpcServer.Start(
                new TestService(), TransportAddress.Parse($"unix:{path}"), null, cancellationToken);

            var failure = await Assert.That(async () => await GrpcServer.Start(
                    new TestService(), TransportAddress.Parse($"unix:{path}"), null, cancellationToken))
                .Throws<InvalidOperationException>();
            _ = await Assert.That(failure).IsNotNull();
            _ = await Assert.That(failure?.Message ?? string.Empty).Contains("active");

            using var client = GrpcTransportClient.Connect(TransportAddress.Parse($"unix:{path}"), null);
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
                new TestService(), TransportAddress.Parse($"unix:{path}"), null, cancellationToken);
            using var client = GrpcTransportClient.Connect(TransportAddress.Parse($"unix:{path}"), null);
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
        var token = TransportToken.Generate();
        await using var server = await GrpcServer.Start(
            new TestService(), TransportAddress.Parse("http://127.0.0.1:0"), token, cancellationToken);
        var address = TransportAddress.Parse(server.Addresses.Single());

        using var authorized = GrpcTransportClient.Connect(address, token);
        _ = await authorized.Client.ListModesAsync(new ListModesRequest(), cancellationToken: cancellationToken);

        using var wrong = GrpcTransportClient.Connect(address, TransportToken.Generate());
        var failure = await Assert.That(async () => await wrong.Client.ListModesAsync(
                new ListModesRequest(), cancellationToken: cancellationToken))
            .Throws<RpcException>();
        _ = await Assert.That(failure).IsNotNull();
        _ = await Assert.That(failure?.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    [Test]
    public async Task Tcp_cannot_start_without_authentication(CancellationToken cancellationToken)
    {
        var failure = await Assert.That(async () => await GrpcServer.Start(
                new TestService(), TransportAddress.Parse("http://127.0.0.1:0"), null, cancellationToken))
            .Throws<InvalidOperationException>();

        _ = await Assert.That(failure).IsNotNull();
        _ = await Assert.That(failure?.Message ?? string.Empty).Contains("bearer token");
    }

    [Test]
    public async Task Transport_addresses_are_explicit()
    {
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
            _ = await Assert.That(async () => await GrpcServer.StartLocal(new TestService(), path, cancellationToken))
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
        var root = TemporaryRoot();
        var path = Path.Combine(root, "parrot.sock");
        try
        {
            await using (var server = await GrpcServer.StartLocal(new TestService(), path, cancellationToken))
            {
                using (var client = GrpcTransportClient.Connect(TransportAddress.Parse($"unix:{path}"), null))
                {
                    var session = await client.Attach(new AttachSessionRequest { UserSessionId = "existing" }, cancellationToken);
                    _ = await Assert.That(session.Id).IsEqualTo("existing");
                }

                using var another = GrpcTransportClient.Connect(TransportAddress.Parse($"unix:{path}"), null);
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
        var root = TemporaryRoot();
        var path = Path.Combine(root, "parrot.sock");
        try
        {
            var server = await GrpcServer.StartLocal(new TestService(), path, cancellationToken);
            using var client = GrpcTransportClient.Connect(TransportAddress.Parse($"unix:{path}"), null);
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
        var path = Path.Combine(TemporaryRoot(), "missing.sock");
        using var client = GrpcTransportClient.Connect(TransportAddress.Parse($"unix:{path}"), null);
        _ = await Assert.That(async () => await client.Attach(
            new AttachSessionRequest { UserSessionId = "existing" }, cancellationToken)).Throws<TimeoutException>();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await cancelled.CancelAsync();
        _ = await Assert.That(async () => await client.Attach(
            new AttachSessionRequest { UserSessionId = "existing" }, cancelled.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Attach_retries_readiness_and_rejects_wrong_session(CancellationToken cancellationToken)
    {
        var root = TemporaryRoot();
        var path = Path.Combine(root, "parrot.sock");
        try
        {
            using var client = GrpcTransportClient.Connect(TransportAddress.Parse($"unix:{path}"), null);
            var attaching = client.Attach(new AttachSessionRequest { UserSessionId = "existing" }, cancellationToken);
            await using var server = await GrpcServer.StartLocal(new TestService(), path, cancellationToken);
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
        var root = TemporaryRoot();
        var path = Path.Combine(root, "parrot.sock");
        try
        {
            _ = await Assert.That(async () => await GrpcServer.StartLocal(
                new TestService(), Path.Combine(root, new string('x', 150) + ".sock"), cancellationToken))
                .Throws<InvalidOperationException>();
            await using (var server = await GrpcServer.StartLocal(new TestService(), path, cancellationToken))
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
