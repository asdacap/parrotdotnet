using Grpc.Core;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class ProviderCallUsageStreamReader(
    IAsyncStreamReader<Event> source,
    Func<ProviderCallUsage, CancellationToken, Task> observe) : IAsyncStreamReader<Event>
{
    public Event Current { get; private set; } = new();

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        while (await source.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            var published = source.Current;
            if (published.PayloadCase != Event.PayloadOneofCase.ProviderCallUsage)
            {
                Current = published;
                return true;
            }

            await observe(published.ProviderCallUsage, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }
}
