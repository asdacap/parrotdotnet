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

    private static string TemporaryRoot() =>
        Path.Combine(Path.GetTempPath(), "pt", Guid.NewGuid().ToString("n", System.Globalization.CultureInfo.InvariantCulture));

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
        public override Task<ListModesResponse> ListModes(ListModesRequest request, ServerCallContext context) =>
            Task.FromResult(new ListModesResponse());
    }
}
