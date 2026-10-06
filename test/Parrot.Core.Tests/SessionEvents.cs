using Parrot.Events;
using Parrot.Protocol;

namespace Parrot.Core.Tests;

// Waits on what a client following Listen sees, not on the drain, which
// nothing outside the session can observe. Subscribe before the send: the
// broker does not replay what was published before a subscription existed.
internal static class SessionEvents
{
    public static Task<Event> TurnEnding(
        this IEventSubscription subscription,
        string agentSessionId,
        CancellationToken cancellationToken) =>
        subscription.Next(
            agentSessionId,
            cancellationToken,
            Event.PayloadOneofCase.TurnEnded,
            Event.PayloadOneofCase.TurnFailed);

    public static async Task<Event> Next(
        this IEventSubscription subscription,
        string agentSessionId,
        CancellationToken cancellationToken,
        params Event.PayloadOneofCase[] payloads)
    {
        await foreach (var published in subscription.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (published.AgentSessionId == agentSessionId && payloads.Contains(published.PayloadCase))
            {
                return published;
            }
        }

        throw new InvalidOperationException("the subscription completed before the event was published");
    }
}
