using System.Diagnostics;
using Parrot.Agent;
using Parrot.Diagnostics;

namespace Parrot.Queues;

internal sealed class AgentQueues(
    AgentQueueCatalog catalog,
    AgentIdentity identity,
    AgentQueues? parent,
    string directory,
    bool deleteDirectory,
    IDiagnosticLog diagnostics) : IDisposable
{
    private readonly SemaphoreSlim _delivery = new(1, 1);
    private IAgentSession? _session;
    private int _disposed;

    public string SessionId => Identity.SessionId;

    internal AgentIdentity Identity { get; } = identity;

    internal AgentQueues? Parent { get; } = parent;

    internal QueueStore Local { get; } = new(directory);

    public void Attach(IAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _session = _session is null
            ? session
            : throw new InvalidOperationException("The queue owner already has an agent session.");
    }

    public QueueInfo Create(string name, string description) => catalog.Create(this, name, description);

    public QueueInfo Get(string name)
    {
        var store = Resolve(name);
        return store.Get(name, SessionId);
    }

    public IReadOnlyList<QueueInfo> List()
    {
        var own = Local.List(SessionId);
        if (Parent is null)
        {
            return own;
        }

        return [.. own.Concat(Parent.Local.List(SessionId)).OrderBy(queue => queue.Name, StringComparer.Ordinal)];
    }

    public async Task<QueueInfo> Listen(string name, bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = Resolve(name);
        var result = store.Monitor(name, SessionId, enabled);
        if (enabled)
        {
            try
            {
                _ = await Deliver(store, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        return result;
    }

    public async Task<QueueInfo> Push(
        string name,
        IReadOnlyList<string> items,
        QueueDirection direction,
        bool close,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = Resolve(name);
        var result = store.Push(name, items, direction, close);
        if (close)
        {
            diagnostics.Write(new DiagnosticEvent("queue", "closed", DiagnosticSeverity.Information)
            {
                AgentSessionId = SessionId,
                Outcome = "closed",
            });
        }

        if (items.Count > 0)
        {
            try
            {
                await catalog.Notify(store, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        return result with { Monitored = store.ListenerSessionIds(name).Contains(SessionId, StringComparer.Ordinal) };
    }

    public QueueTryTakeResult TryTake(string name, int count, QueueDirection direction)
    {
        var store = Resolve(name);
        try
        {
            var result = store.TryTake(name, count, direction);
            return result.Info is null
                ? result
                : result with { Info = WithListeningState(store, name, result.Info) };
        }
        catch (QueueEmptyException failure) when (failure.Info is not null)
        {
            throw new QueueEmptyException(WithListeningState(store, name, failure.Info));
        }
    }

    public async Task<bool> Deliver(CancellationToken cancellationToken)
    {
        if (await Deliver(Local, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        return Parent is not null && await Deliver(Parent.Local, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        catalog.Unregister(this);
        _delivery.Wait();
        try
        {
            Parent?.Local.RemoveListener(SessionId);
            Local.Dispose();

            if (deleteDirectory && System.IO.Directory.Exists(Local.Directory))
            {
                System.IO.Directory.Delete(Local.Directory, recursive: true);
            }
        }
        finally
        {
            _delivery.Dispose();
        }
    }

    internal bool CanAccess(QueueStore store) => ReferenceEquals(Local, store) || ReferenceEquals(Parent?.Local, store);

    internal async Task<bool> Deliver(QueueStore store, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0 || _session is null)
        {
            return false;
        }

        var session = _session;
        await _delivery.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0
                || (!session.IsIdle() && !session.IsWaitingForIncomingInput()))
            {
                return false;
            }

            var started = Stopwatch.GetTimestamp();
            try
            {
                var delivered = await store.DeliverMonitored(SessionId, session.ReceiveQueueNotification, cancellationToken)
                    .ConfigureAwait(false);
                if (delivered)
                {
                    diagnostics.Write(new DiagnosticEvent("queue", "delivery_completed", DiagnosticSeverity.Information)
                    {
                        AgentSessionId = SessionId,
                        DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        Outcome = "delivered",
                    });
                }

                return delivered;
            }
            catch (Exception failure)
            {
                diagnostics.Write(new DiagnosticEvent("queue", "delivery_completed", failure is OperationCanceledException ? DiagnosticSeverity.Information : DiagnosticSeverity.Error)
                {
                    AgentSessionId = SessionId,
                    DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    Outcome = failure is OperationCanceledException ? "cancelled" : "failed",
                    ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
                });
                throw;
            }
        }
        finally
        {
            _ = _delivery.Release();
        }
    }

    private QueueInfo WithListeningState(QueueStore store, string name, QueueInfo info) =>
        info with { Monitored = store.ListenerSessionIds(name).Contains(SessionId, StringComparer.Ordinal) };

    private QueueStore Resolve(string name)
    {
        try
        {
            _ = Local.Get(name, SessionId);
            return Local;
        }
        catch (QueueNotFoundException) when (Parent is not null)
        {
            try
            {
                _ = Parent.Local.Get(name, SessionId);
                return Parent.Local;
            }
            catch (QueueNotFoundException)
            {
            }
        }

        throw new QueueNotFoundException($"queue: '{name}' was not found");
    }
}
