using Parrot.Agent;

namespace Parrot.Queues;

/// <summary>Persists queues and listener state, serializing access and publishing attached inventory changes.</summary>
internal interface IQueueStore : IDisposable
{
    string Directory { get; }

    /// <summary>Registers the owner's existing nonempty queues and forwards subsequent inventory changes.</summary>
    void AttachInventory(AgentIdentity owner, IQueueInventory inventory);

    QueueInfo Create(string name, string description);

    /// <summary>Pushes items at the requested end and optionally closes the queue.</summary>
    QueueInfo Push(string name, IReadOnlyList<string> items, QueueDirection direction, bool close);

    /// <summary>Acquires the queue lock and removes up to the requested number of items.</summary>
    Task<QueueTakeResult> Take(string name, int count, QueueDirection direction, CancellationToken cancellationToken);

    /// <summary>Attempts to acquire the queue lock without waiting and removes available items.</summary>
    QueueTryTakeResult TryTake(string name, int count, QueueDirection direction);

    /// <summary>Reads queue metadata with the specified listener's monitoring state.</summary>
    QueueInfo Get(string name, string listenerSessionId);

    /// <summary>Lists queue metadata with the specified listener's monitoring state.</summary>
    IReadOnlyList<QueueInfo> List(string listenerSessionId);

    /// <summary>Enables or disables the named listener while preserving other listeners.</summary>
    QueueInfo Monitor(string name, string listenerSessionId, bool enabled);

    /// <summary>Restores root listening state, migrating legacy monitoring and removing stale listeners.</summary>
    void AdoptRootListener(string listenerSessionId);

    /// <summary>Removes a listener and its reserved deliveries from every owned queue.</summary>
    void RemoveListener(string listenerSessionId);

    IReadOnlyList<string> ListListenerSessionIds(string name);

    IReadOnlyList<string> ListAllListenerSessionIds();

    /// <summary>Delivers at most one monitored item, removing it only after the recipient accepts it.</summary>
    Task<bool> DeliverMonitored(
        string listenerSessionId,
        Func<QueueNotification, CancellationToken, Task<bool>> deliver,
        CancellationToken cancellationToken);
}
