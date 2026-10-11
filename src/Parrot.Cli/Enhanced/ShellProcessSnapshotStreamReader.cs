using Grpc.Core;
using Parrot.Process;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class ShellProcessSnapshotStreamReader(
    IAsyncStreamReader<Event> source,
    Func<ToolSnapshot, ShellProcessSnapshot, CancellationToken, Task> replace) : IAsyncStreamReader<Event>
{
    private readonly Dictionary<string, OwnerInventory> _owners = new(StringComparer.Ordinal);

    public Event Current { get; private set; } = new();

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        while (await source.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            var published = source.Current;
            if (published.ToolSnapshot?.Kind != ShellProcessInventoryProtocol.Kind)
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

            if (owner.Observe(inventory, ShellProcessSnapshot.Parser.ParseFrom(inventory.Data)) is { } completed)
            {
                await replace(inventory, completed, cancellationToken).ConfigureAwait(false);
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
        private ToolSnapshot? _first;
        private ulong _revision;
        private uint _nextChunk;
        private bool _observed;
        private bool _removed;

        public ShellProcessSnapshot? Observe(ToolSnapshot inventory, ShellProcessSnapshot snapshot)
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
                _revision = inventory.Revision;
                _observed = true;
            }

            if (_first is null
                || !string.Equals(_instance, inventory.InventoryInstanceId, StringComparison.Ordinal)
                || inventory.Revision != _revision
                || inventory.ChunkIndex != _nextChunk
                || inventory.Removed != _first.Removed)
            {
                return null;
            }

            _processes.AddRange(snapshot.Processes);
            _completedProcesses.AddRange(snapshot.CompletedProcesses);
            _nextChunk++;
            if (!inventory.FinalChunk)
            {
                return null;
            }

            var completed = new ShellProcessSnapshot();
            completed.Processes.AddRange(_processes);
            completed.CompletedProcesses.AddRange(_completedProcesses);
            _removed = inventory.Removed;
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
