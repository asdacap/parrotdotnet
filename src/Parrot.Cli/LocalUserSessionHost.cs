using Grpc.Core;
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
        try
        {
            return await GrpcServer.StartLocal(service, session.Resources.SocketPath, session.Diagnostics, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TransportSocketActiveException failure)
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists, failure.Message));
        }
    }
}
