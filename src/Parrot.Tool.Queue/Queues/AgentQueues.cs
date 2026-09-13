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
    private int _disposed;

    public string SessionId => Identity.SessionId;

    public IQueueStore Local { get; } = new QueueStore(identity.Depth == 0
        ? resources.QueueDirectory
        : resources.AgentQueueDirectory(identity.SessionId));

    public Lock Gate => children.Gate;

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    internal AgentIdentity Identity { get; } = identity;

    internal IAgentQueues? Parent { get; } = parent;

    public void Initialize() => Local.AttachInventory(Identity, _inventory);

    public QueueInventorySnapshot CaptureInventory() => _inventory.Capture();

    public IQueueInventorySubscription SubscribeInventory() => _inventory.Subscribe();

    public QueueOwnerSnapshot Snapshot()
    {
        lock (Gate)
        {
            return new(Identity, Volatile.Read(ref _disposed) == 0 ? Local.List() : []);
        }
    }

    public void ValidateParent()
    {
        if (Parent is null)
        {
            return;
        }

        foreach (var queue in Local.List())
        {
            Parent.EnsureMissing(queue.Name);
        }
    }

    public void Attach(IAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
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
                    child.GetService<IAgentQueues>().EnsureMissing(name);
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
        return store.Get(name);
    }

    public IReadOnlyList<QueueInfo> List()
    {
        var own = Local.List();
        if (Parent is null)
        {
            return own;
        }

        return [.. own.Concat(Parent.Local.List()).OrderBy(queue => queue.Name, StringComparer.Ordinal)];
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

        return result;
    }

    public QueueTryTakeResult TryTake(string name, int count, QueueDirection direction)
    {
        var store = Resolve(name);
        return store.TryTake(name, count, direction);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (Parent?.Gate ?? Gate)
        {
            lock (Gate)
            {
                Local.Dispose();
                _inventory.Dispose();
            }
        }

        if (Identity.Depth > 0 && System.IO.Directory.Exists(Local.Directory))
        {
            System.IO.Directory.Delete(Local.Directory, recursive: true);
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
            _ = Local.Get(name);
        }
        catch (QueueNotFoundException)
        {
            return;
        }

        throw new QueueAlreadyExistsException($"queue: '{name}' already exists");
    }

    private IQueueStore Resolve(string name)
    {
        try
        {
            _ = Local.Get(name);
            return Local;
        }
        catch (QueueNotFoundException) when (Parent is not null)
        {
            try
            {
                _ = Parent.Local.Get(name);
                return Parent.Local;
            }
            catch (QueueNotFoundException)
            {
            }
        }

        throw new QueueNotFoundException($"queue: '{name}' was not found");
    }
}
