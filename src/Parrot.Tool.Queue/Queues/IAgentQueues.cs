using Parrot.Agent;

namespace Parrot.Queues;

/// <summary>Owns an agent session's queues and coordinates access with its parent and children.</summary>
internal interface IAgentQueues : IDisposable
{
    string SessionId { get; }

    /// <summary>Gets the shared child-registry lock used to serialize adjacent queue ownership changes.</summary>
    Lock Gate { get; }

    bool IsDisposed { get; }

    /// <summary>Gets the owned store, whose lifetime ends with this queue owner.</summary>
    IQueueStore Local { get; }

    /// <summary>Attaches inventory before the owner is used.</summary>
    void Initialize();

    QueueInventorySnapshot CaptureInventory();

    /// <summary>Subscribes to the current inventory and subsequent snapshots until disposed.</summary>
    IQueueInventorySubscription SubscribeInventory();

    /// <summary>Captures this owner's identity and queues under the ownership lock.</summary>
    QueueOwnerSnapshot Snapshot();

    /// <summary>Rejects local names that already exist in the parent's store.</summary>
    void ValidateParent();

    /// <summary>Validates that this owner is not disposed.</summary>
    void Attach(IAgentSession session);

    /// <summary>Creates an owned queue after checking parent and child name conflicts.</summary>
    QueueInfo Create(string name, string description);

    /// <summary>Reads an owned or parent queue.</summary>
    QueueInfo Get(string name);

    /// <summary>Lists owned and parent queues.</summary>
    IReadOnlyList<QueueInfo> List();

    /// <summary>Pushes items to an accessible queue, optionally closing it.</summary>
    Task<QueueInfo> Push(
        string name,
        IReadOnlyList<string> items,
        QueueDirection direction,
        bool close,
        CancellationToken cancellationToken);

    /// <summary>Attempts a nonblocking take from an owned or parent queue.</summary>
    QueueTryTakeResult TryTake(string name, int count, QueueDirection direction);

    /// <summary>Rejects a name present in this owner's store; disposed owners are ignored.</summary>
    void EnsureMissing(string name);
}
