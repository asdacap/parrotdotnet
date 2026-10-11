using Grpc.Core;
using Parrot.Protocol;
using QueueInventoryProtocol = Parrot.Queues.QueueInventoryProtocol;

namespace Parrot.Cli.Enhanced;

internal sealed class QueueSnapshotStreamReader(
    IAsyncStreamReader<Event> source,
    Func<ToolSnapshot, QueueSnapshot, CancellationToken, Task> replace) : IAsyncStreamReader<Event>
{
    private readonly Dictionary<string, OwnerInventory> _owners = new(StringComparer.Ordinal);

    public Event Current { get; private set; } = new();

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        while (await source.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            var published = source.Current;
            if (published.ToolSnapshot?.Kind != QueueInventoryProtocol.Kind)
            {
                Current = published;
                return true;
            }

            var inventory = published.ToolSnapshot;
            if (string.IsNullOrEmpty(inventory.OwnerAgentSessionId) || string.IsNullOrEmpty(inventory.InventoryInstanceId))
            {
                continue;
            }

            if (!_owners.TryGetValue(inventory.OwnerAgentSessionId, out var owner))
            {
                owner = new OwnerInventory();
                _owners.Add(inventory.OwnerAgentSessionId, owner);
            }

            if (owner.Observe(inventory, QueueSnapshot.Parser.ParseFrom(inventory.Data)) is { } completed)
            {
                await replace(inventory, completed, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    private sealed class OwnerInventory
    {
        private readonly HashSet<string> _retiredInstances = new(StringComparer.Ordinal);
        private readonly List<QueueState> _queues = [];
        private string? _instance;
        private ToolSnapshot? _first;
        private string? _rootAgentSessionId;
        private ulong _revision;
        private uint _nextChunk;
        private bool _observed;
        private bool _removed;

        public QueueSnapshot? Observe(ToolSnapshot inventory, QueueSnapshot snapshot)
        {
            if (_retiredInstances.Contains(inventory.InventoryInstanceId))
            {
                return null;
            }

            if (inventory.ChunkIndex == 0)
            {
                if (string.Equals(_instance, inventory.InventoryInstanceId, StringComparison.Ordinal))
                {
                    if (_removed || (_observed && inventory.Revision <= _revision))
                    {
                        return null;
                    }
                }
                else
                {
                    if (_instance is not null)
                    {
                        _ = _retiredInstances.Add(_instance);
                    }

                    _instance = inventory.InventoryInstanceId;
                    _removed = false;
                }

                ResetStaging();
                _first = inventory;
                _rootAgentSessionId = snapshot.RootAgentSessionId;
                _revision = inventory.Revision;
                _observed = true;
            }

            if (_first is null
                || !string.Equals(_instance, inventory.InventoryInstanceId, StringComparison.Ordinal)
                || inventory.Revision != _revision
                || inventory.ChunkIndex != _nextChunk
                || inventory.Removed != _first.Removed
                || !string.Equals(snapshot.RootAgentSessionId, _rootAgentSessionId, StringComparison.Ordinal))
            {
                return null;
            }

            _queues.AddRange(snapshot.Queues);
            _nextChunk++;
            if (!inventory.FinalChunk)
            {
                return null;
            }

            var completed = new QueueSnapshot { RootAgentSessionId = snapshot.RootAgentSessionId };
            completed.Queues.AddRange(_queues);
            _removed = inventory.Removed;
            ResetStaging();
            return completed;
        }

        private void ResetStaging()
        {
            _queues.Clear();
            _first = null;
            _nextChunk = 0;
        }
    }
}
