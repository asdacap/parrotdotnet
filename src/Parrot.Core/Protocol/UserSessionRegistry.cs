using Grpc.Core;
using Parrot.Diagnostics;

namespace Parrot.Protocol;

internal sealed class UserSessionRegistry : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HostedSession> _sessions = new(StringComparer.Ordinal);
    private bool _accepting = true;
    private int _opening;
    private SemaphoreSlim? _openingsSettled;
    private Task? _shutdown;

    public async Task<Agent.UserSession> Host(
        Func<Task<Agent.UserSession>> open,
        Func<Agent.UserSession, Task<IAsyncDisposable>> host)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(host);

        lock (_gate)
        {
            EnsureAccepting();

            if (_opening == 0)
            {
                _openingsSettled?.Dispose();
                _openingsSettled = new SemaphoreSlim(0, 1);
            }

            _opening++;
        }

        HostedSession created;

        try
        {
            var session = await open().ConfigureAwait(false);
            try
            {
                created = new HostedSession(session, await host(session).ConfigureAwait(false));
            }
            catch (Exception failure)
            {
                session.Diagnostics.Write(new DiagnosticEvent(
                    "transport", "host_failure", failure is OperationCanceledException ? DiagnosticSeverity.Information : DiagnosticSeverity.Error)
                {
                    ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
                });
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            CompleteOpening();
            throw;
        }

        StatusCode refusal;
        string message;

        lock (_gate)
        {
            if (!_accepting)
            {
                refusal = StatusCode.Unavailable;
                message = "the service is shutting down";
            }
            else if (!_sessions.TryAdd(created.Session.Id, created))
            {
                refusal = StatusCode.AlreadyExists;
                message = "the user session is already hosted";
            }
            else
            {
                CompleteOpeningUnderLock();
                return created.Session;
            }
        }

        try
        {
            await created.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            CompleteOpening();
        }

        throw new RpcException(new Status(refusal, message));
    }

    public Agent.UserSession Find(string id)
    {
        lock (_gate)
        {
            EnsureAccepting();
            return _sessions.TryGetValue(id, out var found)
                ? found.Session
                : throw new RpcException(new Status(StatusCode.NotFound, $"no user session {id}"));
        }
    }

    public bool Contains(string id)
    {
        lock (_gate)
        {
            return _accepting && _sessions.ContainsKey(id);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_shutdown is null)
            {
                _accepting = false;
                var sessions = _sessions.Values.ToArray();
                _sessions.Clear();
                _shutdown = Shutdown(sessions, _opening == 0 ? null : _openingsSettled);
            }

            return new ValueTask(_shutdown);
        }
    }

    private static async Task Dispose(HostedSession session) =>
        await session.DisposeAsync().ConfigureAwait(false);

    private static async Task Shutdown(IReadOnlyList<HostedSession> sessions, SemaphoreSlim? openingsSettled)
    {
        var disposal = sessions.Select(Dispose);

        try
        {
            if (openingsSettled is null)
            {
                await Task.WhenAll(disposal).ConfigureAwait(false);
                return;
            }

            await Task.WhenAll(disposal.Append(openingsSettled.WaitAsync())).ConfigureAwait(false);
        }
        finally
        {
            openingsSettled?.Dispose();
        }
    }

    private void CompleteOpening()
    {
        lock (_gate)
        {
            CompleteOpeningUnderLock();
        }
    }

    private void CompleteOpeningUnderLock()
    {
        _opening--;

        if (_opening == 0)
        {
            _ = _openingsSettled?.Release();
        }
    }

    private void EnsureAccepting()
    {
        if (!_accepting)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "the service is shutting down"));
        }
    }

    private sealed class HostedSession(Agent.UserSession session, IAsyncDisposable listener) : IAsyncDisposable
    {
        public Agent.UserSession Session { get; } = session;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await listener.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await Session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
