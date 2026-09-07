namespace Parrot.Protocol;

/// <summary>Provides transport hosting for an existing user session without taking ownership of the session.</summary>
internal interface IUserSessionHost
{
    /// <summary>
    /// Starts hosting with cancellable setup and returns a caller-owned hosting lifetime.
    /// Disposing that lifetime stops hosting; implementations may leave the session unexposed.
    /// </summary>
    Task<IAsyncDisposable> Host(
        Agent.UserSession session,
        Parrot.ParrotBase service,
        CancellationToken cancellationToken);
}
