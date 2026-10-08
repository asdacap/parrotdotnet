using Grpc.Core;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

// The main agent's queued prompts run as owned executions, which announce
// themselves the way a subagent does; its turns already say all of that.
internal sealed class RootLifecycleFilterStreamReader(IAsyncStreamReader<Event> source) : IAsyncStreamReader<Event>
{
    public Event Current { get; private set; } = new();

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        while (await source.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            var published = source.Current;
            var parentAgentSessionId = published.PayloadCase switch
            {
                Event.PayloadOneofCase.AgentStarted => published.AgentStarted.ParentAgentSessionId,
                Event.PayloadOneofCase.AgentFinished => published.AgentFinished.ParentAgentSessionId,
                Event.PayloadOneofCase.AgentFailed => published.AgentFailed.ParentAgentSessionId,
                _ => null,
            };
            if (parentAgentSessionId is { Length: 0 })
            {
                continue;
            }

            Current = published;
            return true;
        }

        return false;
    }
}
