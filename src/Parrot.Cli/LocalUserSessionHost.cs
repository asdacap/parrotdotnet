using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal sealed class LocalUserSessionHost : IUserSessionHost
{
    public async Task<IAsyncDisposable> Host(
        Agent.UserSession session,
        GeneratedParrot.ParrotBase service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return await GrpcServer.StartLocal(service, session.Resources.SocketPath, cancellationToken)
            .ConfigureAwait(false);
    }
}
