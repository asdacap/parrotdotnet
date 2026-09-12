using System.Diagnostics;
using Parrot.Agent;
using Parrot.Diagnostics;
using Parrot.Store;

namespace Parrot.Queues;

internal sealed class AgentQueues(
    AgentIdentity identity,
    IAgentQueues? parent,
    UserSessionResources resources,
    IChildRegistry children,
    Func<AgentIdentity, IQueueInventory> createInventory,
    IDiagnosticLog diagnostics) : IAgentQueues
{
    private readonly IQueueInventory _inventory = createInventory(identity);
    private readonly SemaphoreSlim _delivery = new(1, 1);
    private IAgentSession? _session;
    private int _disposed;

    public string SessionId => Identity.SessionId;

    public IQueueStore Local { get; } = new QueueStore(identity.Depth == 0
        ? resources.QueueDirectory
        : resources.AgentQueueDirectory(identity.SessionId));

    public Lock Gate => children.Gate;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal AgentIdentity Identity { get; } = identity;

    internal IAgentQueues? Parent { get; } = parent;

    public void Initialize()
    {
        Local.AttachInventory(Identity, _inventory);
        if (Identity.Depth == 0)
        {
            Local.AdoptRootListener(SessionId);
        }
    }

    public QueueInventorySnapshot CaptureInventory() => _inventory.Capture();

    public IQueueInventorySubscription SubscribeInventory() => _inventory.Subscribe();

    public QueueOwnerSnapshot Snapshot()
    {
        lock (Gate)
        {
            return new(Identity, Volatile.Read(ref _disposed) == 0 ? Local.List(SessionId) : []);
        }
    }

    public void ValidateParent()
    {
        if (Parent is null)
        {
            return;
        }

        foreach (var queue in Local.List(SessionId))
        {
            Parent.EnsureMissing(queue.Name);
        }
    }

    public void Attach(IAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _session = _session is null
            ? session
            : throw new InvalidOperationException("The queue owner already has an agent session.");
    }

    public QueueInfo Create(string name, string description)
    {
        QueueInfo created;
        lock (Parent?.Gate ?? Gate)
        {
            lock (Gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (Parent is not null)
                {
                    ObjectDisposedException.ThrowIf(Parent.IsDisposed, Parent);
                }

                Parent?.EnsureMissing(name);
                foreach (var child in children.SnapshotChildScopes())
                {
                    child.Queues.EnsureMissing(name);
                }

                created = Local.Create(name, description);
            }
        }

        diagnostics.Write(new DiagnosticEvent("queue", "created", DiagnosticSeverity.Information)
        {
            AgentSessionId = SessionId,
            Outcome = "created",
        });
        return created;
    }

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
                _ = await DeliverFromStore(store, cancellationToken).ConfigureAwait(false);
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
                await (ReferenceEquals(store, Local) ? this : Parent
                    ?? throw new InvalidOperationException("Queue store owner is missing.")).Notify(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        return result with { Monitored = store.ListListenerSessionIds(name).Contains(SessionId, StringComparer.Ordinal) };
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
        if (await DeliverFromStore(Local, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        return Parent is not null && await DeliverFromStore(Parent.Local, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _delivery.Wait();
        try
        {
            lock (Parent?.Gate ?? Gate)
            {
                lock (Gate)
                {
                    try
                    {
                        if (Parent is not null && !Parent.IsDisposed)
                        {
                            Parent.Local.RemoveListener(SessionId);
                        }
                    }
                    finally
                    {
                        try
                        {
                            Local.Dispose();
                        }
                        finally
                        {
                            _inventory.Dispose();
                        }
                    }
                }
            }

            if (Identity.Depth > 0 && System.IO.Directory.Exists(Local.Directory))
            {
                System.IO.Directory.Delete(Local.Directory, recursive: true);
            }
        }
        finally
        {
            _delivery.Dispose();
        }
    }

    public async Task<bool> DeliverFromStore(IQueueStore store, CancellationToken cancellationToken)
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

    public async Task Notify(CancellationToken cancellationToken)
    {
        var candidates = children.SnapshotChildScopes().Select(static child => child.Queues).Prepend(this);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await candidate.DeliverFromStore(Local, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    public void EnsureMissing(string name)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            _ = Local.Get(name, SessionId);
        }
        catch (QueueNotFoundException)
        {
            return;
        }

        throw new QueueAlreadyExistsException($"queue: '{name}' already exists");
    }

    private QueueInfo WithListeningState(IQueueStore store, string name, QueueInfo info) =>
        info with { Monitored = store.ListListenerSessionIds(name).Contains(SessionId, StringComparer.Ordinal) };

    private IQueueStore Resolve(string name)
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
