using Grpc.Core;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class ShellProcessSnapshotStreamReader(
    IAsyncStreamReader<Event> source,
    Func<ShellProcessSnapshot, CancellationToken, Task> replace) : IAsyncStreamReader<Event>
{
    private readonly HashSet<string> _retiredInstances = new(StringComparer.Ordinal);
    private readonly List<ActiveShellProcess> _staged = [];
    private string? _acceptedInstance;
    private string? _stagedInstance;
    private uint _chunkCount;
    private uint _nextChunk;
    private ulong _acceptedRevision;
    private ulong _stagedRevision;

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

            await Observe(published.ShellProcessSnapshot, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task Observe(ShellProcessSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.ChunkCount == 0 || snapshot.ChunkIndex >= snapshot.ChunkCount)
        {
            ResetStaging();
            return;
        }

        if (snapshot.ChunkIndex == 0)
        {
            if (_retiredInstances.Contains(snapshot.InventoryInstanceId)
                || (string.Equals(_acceptedInstance, snapshot.InventoryInstanceId, StringComparison.Ordinal)
                    && snapshot.Revision <= _acceptedRevision))
            {
                ResetStaging();
                return;
            }

            _staged.Clear();
            _stagedInstance = snapshot.InventoryInstanceId;
            _stagedRevision = snapshot.Revision;
            _chunkCount = snapshot.ChunkCount;
            _nextChunk = 0;
        }

        if (_stagedInstance is null
            || !string.Equals(_stagedInstance, snapshot.InventoryInstanceId, StringComparison.Ordinal)
            || snapshot.Revision != _stagedRevision
            || snapshot.ChunkCount != _chunkCount
            || snapshot.ChunkIndex != _nextChunk)
        {
            ResetStaging();
            return;
        }

        _staged.AddRange(snapshot.Processes.Select(static process => process.Clone()));
        _nextChunk++;
        if (_nextChunk != _chunkCount)
        {
            return;
        }

        var completed = new ShellProcessSnapshot
        {
            InventoryInstanceId = _stagedInstance,
            Revision = _stagedRevision,
            ChunkIndex = 0,
            ChunkCount = 1,
        };
        completed.Processes.AddRange(_staged);
        if (_acceptedInstance is not null
            && !string.Equals(_acceptedInstance, completed.InventoryInstanceId, StringComparison.Ordinal))
        {
            _ = _retiredInstances.Add(_acceptedInstance);
        }

        _acceptedInstance = completed.InventoryInstanceId;
        _acceptedRevision = completed.Revision;
        ResetStaging();
        await replace(completed, cancellationToken).ConfigureAwait(false);
    }

    private void ResetStaging()
    {
        _staged.Clear();
        _stagedInstance = null;
        _stagedRevision = 0;
        _chunkCount = 0;
        _nextChunk = 0;
    }
}
