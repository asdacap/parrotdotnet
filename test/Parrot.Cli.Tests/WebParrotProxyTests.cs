using Grpc.Core;
using Parrot.Cli.Web;
using Parrot.Protocol;
using Parrot.State;
using Parrot.Store;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class WebParrotProxyTests
{
    private const string RemoteId = "user-session-remote";
    private const string LocalId = "user-session-local";

    [Test]
    public async Task A_session_live_in_another_process_is_attached_through_its_socket(CancellationToken cancellationToken)
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        var root = Path.Combine(OperatingSystem.IsMacOS() ? "/tmp" : Path.GetTempPath(), "pw", Guid.NewGuid().ToString("n"));
        var workingDirectory = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
        var paths = new StatePaths(Path.Combine(root, "state"), root, root);
        var resources = new UserSessionResources(
            paths, UserSessionId.Parse(RemoteId), ProjectWorkspace.FromLaunchDirectory(workingDirectory));
        _ = Directory.CreateDirectory(resources.Root);
        var remote = new RemoteService();
        var local = new ScriptedInvoker();
        try
        {
            await using var server = await GrpcServer.StartLocal(remote, resources.SocketPath, diagnostics.Log, cancellationToken);
            using var router = new WebSessionRouter(
                new GeneratedParrot.ParrotClient(local), paths, workingDirectory, TextWriter.Null, diagnostics.Log);
            var proxy = new WebParrotProxy(new LocalService(), router);
            var context = new InProcessServerCallContext(cancellationToken);

            var resumed = await proxy.ResumeSession(
                new ResumeSessionRequest { UserSessionId = RemoteId, WorkingDirectory = workingDirectory }, context);
            _ = await proxy.SendMessage(new SendMessageRequest { UserSessionId = RemoteId, Text = "to remote" }, context);
            _ = await proxy.SendMessage(new SendMessageRequest { UserSessionId = LocalId, Text = "to local" }, context);

            _ = await Assert.That(resumed.Id).IsEqualTo(RemoteId);
            _ = await Assert.That(router.IsRemote(RemoteId)).IsTrue();
            _ = await Assert.That(remote.Sent).IsEquivalentTo(["to remote"]);
            _ = await Assert.That(local.Sent).IsEquivalentTo(["to local"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Hosts nothing, as when the session is live in another process.
    private sealed class LocalService : GeneratedParrot.ParrotBase
    {
        public override Task<UserSession> ResumeSession(ResumeSessionRequest request, ServerCallContext context) =>
            throw new RpcException(new Status(StatusCode.AlreadyExists, "the session is open elsewhere"));

        public override Task<UserSession> AttachSession(AttachSessionRequest request, ServerCallContext context) =>
            throw new RpcException(new Status(StatusCode.NotFound, "no user session"));
    }

    private sealed class RemoteService : GeneratedParrot.ParrotBase
    {
        private readonly List<string> _sent = [];

        public IReadOnlyList<string> Sent => _sent;

        public override Task<UserSession> AttachSession(AttachSessionRequest request, ServerCallContext context) =>
            Task.FromResult(new UserSession { Id = request.UserSessionId });

        public override Task<SendMessageResponse> SendMessage(SendMessageRequest request, ServerCallContext context)
        {
            _sent.Add(request.Text);
            return Task.FromResult(new SendMessageResponse());
        }
    }
}
