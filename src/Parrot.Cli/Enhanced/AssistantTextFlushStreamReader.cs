using Grpc.Core;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class AssistantTextFlushStreamReader(
    IAsyncStreamReader<Event> source,
    AssistantTextFlushCoordinator coordinator) : IAsyncStreamReader<Event>
{
    public Event Current => source.Current;

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        if (!await source.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await coordinator.Prepare(source.Current, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
