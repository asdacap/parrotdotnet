using Parrot.Agent;

namespace Parrot.Queues;

/// <summary>Persists queues, serializing access and publishing attached inventory changes.</summary>
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

    /// <summary>Reads queue metadata.</summary>
    QueueInfo Get(string name);

    /// <summary>Lists queue metadata.</summary>
    IReadOnlyList<QueueInfo> List();
}
