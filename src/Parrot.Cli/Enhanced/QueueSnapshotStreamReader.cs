using Grpc.Core;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class QueueSnapshotStreamReader(
    IAsyncStreamReader<Event> source,
    Func<IReadOnlyList<QueueState>, CancellationToken, Task> replace) : IAsyncStreamReader<Event>
{
    private readonly List<QueueState> _staged = [];
    private uint _nextChunk;
    private ulong _revision;
    private bool _staging;

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

            await Observe(published.QueueSnapshot, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task Observe(QueueSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!_staging || snapshot.Revision != _revision || snapshot.ChunkIndex != _nextChunk)
        {
            _staged.Clear();
            _staging = snapshot.ChunkIndex == 0;
            _revision = snapshot.Revision;
            _nextChunk = 0;
        }

        if (!_staging || snapshot.ChunkIndex != _nextChunk)
        {
            return;
        }

        _staged.AddRange(snapshot.Queues.Select(queue => queue.Clone()));
        _nextChunk++;
        if (!snapshot.FinalChunk)
        {
            return;
        }

        var completed = _staged.ToArray();
        _staged.Clear();
        _staging = false;
        await replace(completed, cancellationToken).ConfigureAwait(false);
    }
}
