using Grpc.Core;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class SessionUsageSnapshotStreamReader(
    IAsyncStreamReader<Event> source,
    Func<SessionUsageSnapshot, CancellationToken, Task> observe) : IAsyncStreamReader<Event>
{
    public Event Current { get; private set; } = new();

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        while (await source.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            var published = source.Current;
            if (published.PayloadCase != Event.PayloadOneofCase.SessionUsageSnapshot)
            {
                Current = published;
                return true;
            }

            await observe(published.SessionUsageSnapshot, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }
}
