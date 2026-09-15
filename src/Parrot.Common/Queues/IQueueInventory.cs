namespace Parrot.Queues;

/// <summary>Tracks one owner's nonempty queues and publishes revisioned inventory snapshots.</summary>
internal interface IQueueInventory : IDisposable
{
    /// <summary>Registers the owner and its initial queues once.</summary>
    void RegisterOwner(IReadOnlyList<QueueState> queues);

    /// <summary>Updates a registered queue, removing it from inventory when empty.</summary>
    void Update(QueueState state);

    /// <summary>Removes the owner and all its queues from inventory.</summary>
    void UnregisterOwner();

    /// <summary>Captures the latest inventory, including its revision and removal state.</summary>
    QueueInventorySnapshot Capture();

    /// <summary>Subscribes to the current inventory and subsequent snapshots until disposed.</summary>
    IQueueInventorySubscription Subscribe();
}
