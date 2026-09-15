using Parrot.Agent;

namespace Parrot.Queues;

internal sealed class QueueInventory(AgentIdentity identity) : IQueueInventory
{
    private readonly QueueInventoryFeed _feed = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, QueueState> _queues = new(StringComparer.Ordinal);
    private ulong _revision;
    private bool _registered;
    private bool _disposed;

    public string InstanceId { get; } = $"queue-inventory-{Guid.CreateVersion7():n}";

    public void RegisterOwner(IReadOnlyList<QueueState> queues)
    {
        ArgumentNullException.ThrowIfNull(queues);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_registered)
            {
                throw new InvalidOperationException($"Queue owner '{identity.SessionId}' is already registered.");
            }

            _registered = true;
            var changed = false;
            foreach (var queue in queues)
            {
                var state = queue with { OwnerAgentSessionId = identity.SessionId };
                _queues.Add(state.Name, state);
                changed = true;
            }

            PublishLocked(changed);
        }
    }

    public void Update(QueueState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateOwner(state.OwnerAgentSessionId);

        lock (_gate)
        {
            if (_disposed || !_registered)
            {
                return;
            }

            var changed = state.ItemCount > 0
                ? SetLocked(state)
                : _queues.Remove(state.Name);
            PublishLocked(changed);
        }
    }

    public void UnregisterOwner()
    {
        lock (_gate)
        {
            if (_disposed || !_registered)
            {
                return;
            }

            _registered = false;
            var changed = _queues.Count > 0;
            _queues.Clear();
            PublishLocked(changed);
        }
    }

    public QueueInventorySnapshot Capture()
    {
        lock (_gate)
        {
            return CaptureLocked();
        }
    }

    public IQueueInventorySubscription Subscribe()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _feed.Subscribe(CaptureLocked());
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _registered = false;
            _queues.Clear();
            _revision++;
            _feed.Publish(CaptureLocked());
            _feed.Dispose();
        }
    }

    private QueueInventorySnapshot CaptureLocked() => new(
        identity.SessionId,
        InstanceId,
        _revision,
        _disposed,
        [.. _queues.Values.OrderBy(state => state.Name, StringComparer.Ordinal)]);

    private void PublishLocked(bool changed)
    {
        if (!changed)
        {
            return;
        }

        _revision++;
        _feed.Publish(CaptureLocked());
    }

    private bool SetLocked(QueueState state)
    {
        if (_queues.TryGetValue(state.Name, out var current) && current == state)
        {
            return false;
        }

        _queues[state.Name] = state;
        return true;
    }

    private void ValidateOwner(string ownerAgentSessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerAgentSessionId);
        if (!string.Equals(ownerAgentSessionId, identity.SessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Queue owner '{ownerAgentSessionId}' does not match inventory owner '{identity.SessionId}'.");
        }
    }
}
