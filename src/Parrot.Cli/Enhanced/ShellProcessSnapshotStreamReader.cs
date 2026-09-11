using Grpc.Core;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class ShellProcessSnapshotStreamReader(
    IAsyncStreamReader<Event> source,
    Func<ShellProcessSnapshot, CancellationToken, Task> replace) : IAsyncStreamReader<Event>
{
    private readonly Dictionary<string, OwnerInventory> _owners = new(StringComparer.Ordinal);

    public Event Current { get; private set; } = new();

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        while (await source.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            var published = source.Current;
            if (published.PayloadCase != Event.PayloadOneofCase.ShellProcessSnapshot)
            {
                Current = published;
                return true;
            }

            var snapshot = published.ShellProcessSnapshot;
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
        private readonly List<ActiveShellProcess> _processes = [];
        private readonly List<CompletedShellProcess> _completedProcesses = [];
        private string? _instance;
        private ShellProcessSnapshot? _first;
        private ulong _revision;
        private uint _nextChunk;
        private bool _observed;
        private bool _removed;

        public ShellProcessSnapshot? Observe(ShellProcessSnapshot snapshot)
        {
            if (snapshot.ChunkCount == 0 || snapshot.ChunkIndex >= snapshot.ChunkCount || _retiredInstances.Contains(snapshot.InventoryInstanceId))
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
                || snapshot.ChunkCount != _first.ChunkCount)
            {
                return null;
            }

            _processes.AddRange(snapshot.Processes.Select(static process => process.Clone()));
            _completedProcesses.AddRange(snapshot.CompletedProcesses.Select(static process => process.Clone()));
            _nextChunk++;
            if (_nextChunk != snapshot.ChunkCount)
            {
                return null;
            }

            var completed = snapshot.Clone();
            completed.ChunkIndex = 0;
            completed.Processes.Clear();
            completed.CompletedProcesses.Clear();
            completed.Processes.AddRange(_processes);
            completed.CompletedProcesses.AddRange(_completedProcesses);
            completed.ChunkCount = 1;
            _removed = snapshot.Removed;
            ResetStaging();
            return completed;
        }

        private void ResetStaging()
        {
            _processes.Clear();
            _completedProcesses.Clear();
            _first = null;
            _nextChunk = 0;
        }
    }
}
