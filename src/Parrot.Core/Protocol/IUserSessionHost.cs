namespace Parrot.Protocol;

internal interface IUserSessionHost
{
    Task<IAsyncDisposable> Host(
        Agent.UserSession session,
        Parrot.ParrotBase service,
        CancellationToken cancellationToken);
}
