using System.Collections.Concurrent;
using Parrot.Diagnostics;
using Parrot.Protocol;
using Parrot.State;
using Parrot.Store;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Web;

// Routes each user session to the process hosting it: this one, or another
// parrot process reached through the session's socket, as a terminal CLI
// does when it finds its session already open.
internal sealed class WebSessionRouter(
    GeneratedParrot.ParrotClient local,
    StatePaths paths,
    string workingDirectory,
    TextWriter error,
    IDiagnosticLog diagnostics) : IDisposable
{
    private readonly ConcurrentDictionary<string, Remote> _remotes = new(StringComparer.Ordinal);

    public GeneratedParrot.ParrotClient For(string userSessionId) =>
        _remotes.TryGetValue(userSessionId, out var remote) ? remote.Client : local;

    public bool IsRemote(string userSessionId) => _remotes.ContainsKey(userSessionId);

    public async Task<UserSession> OpenDefault(CancellationToken cancellationToken)
    {
        var startup = new LocalChatStartup(
            paths,
            workingDirectory,
            Environment.MachineName,
            error,
            diagnostics,
            _ => Task.FromResult(local),
            (_, _) => Task.FromResult(new CreateSessionRequest()));
        try
        {
            var (client, session) = await startup.Open(true, cancellationToken).ConfigureAwait(false);
            if (client != local)
            {
                Adopt(session.Id, new Remote(client, startup));
                startup = null;
            }

            return session;
        }
        finally
        {
            startup?.Dispose();
        }
    }

    public async Task<UserSession> AttachRemote(AttachSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resources = new UserSessionResources(
            paths, UserSessionId.Parse(request.UserSessionId), ProjectWorkspace.FromLaunchDirectory(workingDirectory));
        var connection = GrpcTransportClient.Connect(
            TransportAddress.Parse($"unix:{resources.SocketPath}"), null, diagnostics);
        try
        {
            var session = await connection.Attach(request, cancellationToken).ConfigureAwait(false);
            Adopt(session.Id, new Remote(connection.Client, connection));
            connection = null;
            return session;
        }
        finally
        {
            connection?.Dispose();
        }
    }

    public void Forget(string userSessionId)
    {
        if (_remotes.TryRemove(userSessionId, out var remote))
        {
            remote.Owner.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var userSessionId in _remotes.Keys)
        {
            Forget(userSessionId);
        }
    }

    private void Adopt(string userSessionId, Remote remote)
    {
        Forget(userSessionId);
        _remotes[userSessionId] = remote;
    }

    private sealed record Remote(GeneratedParrot.ParrotClient Client, IDisposable Owner);
}
