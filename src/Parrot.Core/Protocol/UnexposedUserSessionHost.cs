namespace Parrot.Protocol;

internal sealed class UnexposedUserSessionHost : IUserSessionHost
{
    public Task<IAsyncDisposable> Host(
        Agent.UserSession session,
        Parrot.ParrotBase service,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IAsyncDisposable>(UnexposedSession.Instance);
    }

    private sealed class UnexposedSession : IAsyncDisposable
    {
        public static UnexposedSession Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
