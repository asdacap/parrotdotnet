using Parrot.Agent;

namespace Parrot.Queues;

/// <summary>Owns an agent session's queues and coordinates access and delivery with its parent and children.</summary>
internal interface IAgentQueues : IDisposable
{
    string SessionId { get; }

    /// <summary>Gets the shared child-registry lock used to serialize adjacent queue ownership changes.</summary>
    Lock Gate { get; }

    bool IsDisposed { get; }

    /// <summary>Gets the owned store, whose lifetime ends with this queue owner.</summary>
    IQueueStore Local { get; }

    /// <summary>Attaches inventory and restores root listeners before the owner is used.</summary>
    void Initialize();

    QueueInventorySnapshot CaptureInventory();

    /// <summary>Subscribes to the current inventory and subsequent snapshots until disposed.</summary>
    IQueueInventorySubscription SubscribeInventory();

    /// <summary>Captures this owner's identity and queues under the ownership lock.</summary>
    QueueOwnerSnapshot Snapshot();

    /// <summary>Rejects local names that already exist in the parent's store.</summary>
    void ValidateParent();

    /// <summary>Attaches the session that receives queue notifications once.</summary>
    void Attach(IAgentSession session);

    /// <summary>Creates an owned queue after checking parent and child name conflicts.</summary>
    QueueInfo Create(string name, string description);

    /// <summary>Reads an owned or parent queue with this session's listening state.</summary>
    QueueInfo Get(string name);

    /// <summary>Lists owned and parent queues with this session's listening state.</summary>
    IReadOnlyList<QueueInfo> List();

    /// <summary>Changes this session's listening state and attempts delivery when enabled.</summary>
    Task<QueueInfo> Listen(string name, bool enabled, CancellationToken cancellationToken);

    /// <summary>Pushes items to an accessible queue, optionally closes it, and notifies eligible listeners.</summary>
    Task<QueueInfo> Push(
        string name,
        IReadOnlyList<string> items,
        QueueDirection direction,
        bool close,
        CancellationToken cancellationToken);

    /// <summary>Attempts a nonblocking take from an owned or parent queue.</summary>
    QueueTryTakeResult TryTake(string name, int count, QueueDirection direction);

    /// <summary>Attempts delivery to this session from its owned store, then its parent's store.</summary>
    Task<bool> Deliver(CancellationToken cancellationToken);

    /// <summary>Serializes notification delivery from the supplied store to this session when eligible.</summary>
    Task<bool> DeliverFromStore(IQueueStore store, CancellationToken cancellationToken);

    /// <summary>Attempts delivery from the owned store to this session, then its children.</summary>
    Task Notify(CancellationToken cancellationToken);

    /// <summary>Rejects a name present in this owner's store; disposed owners are ignored.</summary>
    void EnsureMissing(string name);
}
