using Grpc.Core;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class QueueSnapshotStreamReader(
    IAsyncStreamReader<Event> source,
    Func<QueueSnapshot, CancellationToken, Task> replace) : IAsyncStreamReader<Event>
{
    private readonly Dictionary<string, OwnerInventory> _owners = new(StringComparer.Ordinal);

    public Event Current { get; private set; } = new();

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        while (await source.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            var published = source.Current;
            if (published.PayloadCase != Event.PayloadOneofCase.QueueSnapshot)
            {
                Current = published;
                return true;
            }

            var snapshot = published.QueueSnapshot;
            if (string.IsNullOrEmpty(snapshot.OwnerAgentSessionId) || string.IsNullOrEmpty(snapshot.InventoryInstanceId))
            {
                continue;
            }

            if (!_owners.TryGetValue(snapshot.OwnerAgentSessionId, out var owner))
            {
                owner = new OwnerInventory();
                _owners.Add(snapshot.OwnerAgentSessionId, owner);
            }

            if (owner.Observe(snapshot) is { } completed)
            {
                await replace(completed, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    private sealed class OwnerInventory
    {
        private readonly HashSet<string> _retiredInstances = new(StringComparer.Ordinal);
        private readonly List<QueueState> _queues = [];
        private string? _instance;
        private QueueSnapshot? _first;
        private ulong _revision;
        private uint _nextChunk;
        private bool _observed;
        private bool _removed;

        public QueueSnapshot? Observe(QueueSnapshot snapshot)
        {
            if (_retiredInstances.Contains(snapshot.InventoryInstanceId))
            {
                return null;
            }

            if (snapshot.ChunkIndex == 0)
            {
                if (string.Equals(_instance, snapshot.InventoryInstanceId, StringComparison.Ordinal))
                {
                    if (_removed || (_observed && snapshot.Revision <= _revision))
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

                    _instance = snapshot.InventoryInstanceId;
                    _removed = false;
                }

                ResetStaging();
                _first = snapshot;
                _revision = snapshot.Revision;
                _observed = true;
            }

            if (_first is null
                || !string.Equals(_instance, snapshot.InventoryInstanceId, StringComparison.Ordinal)
                || snapshot.Revision != _revision
                || snapshot.ChunkIndex != _nextChunk
                || snapshot.Removed != _first.Removed
                || !string.Equals(snapshot.RootAgentSessionId, _first.RootAgentSessionId, StringComparison.Ordinal))
            {
                return null;
            }

            _queues.AddRange(snapshot.Queues.Select(static queue => queue.Clone()));
            _nextChunk++;
            if (!snapshot.FinalChunk)
            {
                return null;
            }

            var completed = snapshot.Clone();
            completed.ChunkIndex = 0;
            completed.Queues.Clear();
            completed.Queues.AddRange(_queues);
            completed.FinalChunk = true;
            _removed = snapshot.Removed;
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
