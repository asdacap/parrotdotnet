namespace Parrot.Queues;

internal sealed class QueueInventory : IDisposable
{
    private readonly QueueInventoryFeed _feed = new();
    private readonly Lock _gate = new();
    private readonly HashSet<string> _owners = new(StringComparer.Ordinal);
    private readonly Dictionary<QueueKey, QueueState> _queues = [];
    private ulong _revision;
    private bool _disposed;

    public void RegisterOwner(string ownerAgentSessionId, IReadOnlyList<QueueState> queues)
    {
        ArgumentException.ThrowIfNullOrEmpty(ownerAgentSessionId);
        ArgumentNullException.ThrowIfNull(queues);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_owners.Add(ownerAgentSessionId))
            {
                throw new InvalidOperationException($"Queue owner '{ownerAgentSessionId}' is already registered.");
            }

            var changed = false;
            foreach (var queue in queues)
            {
                var state = queue with { OwnerAgentSessionId = ownerAgentSessionId };
                _queues.Add(new(ownerAgentSessionId, state.Name), state);
                changed = true;
            }

            PublishLocked(changed);
        }
    }

    public void Update(QueueState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (_gate)
        {
            if (_disposed || !_owners.Contains(state.OwnerAgentSessionId))
            {
                return;
            }

            var key = new QueueKey(state.OwnerAgentSessionId, state.Name);
            var changed = state.ItemCount > 0
                ? SetLocked(key, state)
                : _queues.Remove(key);
            PublishLocked(changed);
        }
    }

    public void UnregisterOwner(string ownerAgentSessionId)
    {
        lock (_gate)
        {
            if (_disposed || !_owners.Remove(ownerAgentSessionId))
            {
                return;
            }

            var changed = false;
            foreach (var key in _queues.Keys.Where(key =>
                         string.Equals(key.OwnerAgentSessionId, ownerAgentSessionId, StringComparison.Ordinal)).ToArray())
            {
                changed |= _queues.Remove(key);
            }

            PublishLocked(changed);
        }
    }

    public QueueInventorySubscription Subscribe()
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
            _owners.Clear();
            _queues.Clear();
        }

        _feed.Dispose();
    }

    private QueueInventorySnapshot CaptureLocked() => new(
        _revision,
        [.. _queues.Values
            .OrderBy(state => state.OwnerAgentSessionId, StringComparer.Ordinal)
            .ThenBy(state => state.Name, StringComparer.Ordinal)]);

    private void PublishLocked(bool changed)
    {
        if (!changed)
        {
            return;
        }

        _revision++;
        _feed.Publish(CaptureLocked());
    }

    private bool SetLocked(QueueKey key, QueueState state)
    {
        if (_queues.TryGetValue(key, out var current) && current == state)
        {
            return false;
        }

        _queues[key] = state;
        return true;
    }

    private readonly record struct QueueKey(string OwnerAgentSessionId, string Name);
}
