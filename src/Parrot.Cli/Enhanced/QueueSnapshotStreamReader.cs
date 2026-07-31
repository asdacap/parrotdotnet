using Grpc.Core;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class QueueSnapshotStreamReader(
    IAsyncStreamReader<Event> source,
    Func<string, IReadOnlyList<QueueState>, CancellationToken, Task> replace) : IAsyncStreamReader<Event>
{
    private readonly List<QueueState> _staged = [];
    private uint _nextChunk;
    private ulong _acceptedRevision;
    private ulong _revision;
    private bool _accepted;
    private bool _staging;
    private string _rootAgentSessionId = string.Empty;

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
        if (snapshot.ChunkIndex == 0
            && (!_staging || snapshot.Revision > _revision)
            && (!_accepted || snapshot.Revision > _acceptedRevision))
        {
            _staged.Clear();
            _nextChunk = 0;
            _revision = snapshot.Revision;
            _rootAgentSessionId = snapshot.RootAgentSessionId;
            _staging = true;
        }

        if (!_staging || snapshot.Revision != _revision || snapshot.ChunkIndex != _nextChunk)
        {
            return;
        }

        _staged.AddRange(snapshot.Queues.Select(static queue => queue.Clone()));
        _nextChunk++;
        if (!snapshot.FinalChunk)
        {
            return;
        }

        var completed = _staged.ToArray();
        _staged.Clear();
        _staging = false;
        _accepted = true;
        _acceptedRevision = snapshot.Revision;
        await replace(_rootAgentSessionId, completed, cancellationToken).ConfigureAwait(false);
    }
}
