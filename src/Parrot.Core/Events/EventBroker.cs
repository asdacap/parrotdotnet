using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Parrot.Protocol;

namespace Parrot.Events;

// Fan-out to zero or more subscribers, each with its own bounded queue.
//
// Publishing never blocks and never waits for a reader. A subscriber that stops
// reading -- a client that dropped, a CLI that stopped caring after its turn --
// loses its oldest events rather than stalling the session that is publishing
// to it. That is safe here because these are live events, which principle 10
// makes disposable; durability is EventRepository's job from M2.
//
// A shared bounded channel would have made a dead listener look like a hang.
internal sealed class EventBroker : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<Channel<Event>> _subscribers = [];

    public ValueTask Publish(Event published, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Publish(published);
        return ValueTask.CompletedTask;
    }

    public void Publish(Event published)
    {
        Channel<Event>[] targets;

        lock (_gate)
        {
            targets = [.. _subscribers];
        }

        foreach (var target in targets)
        {
            _ = target.Writer.TryWrite(published);
        }
    }

    public async IAsyncEnumerable<Event> Subscribe(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var queue = Channel.CreateBounded<Event>(
            new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        lock (_gate)
        {
            _subscribers.Add(queue);
        }

        try
        {
            await foreach (var published in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return published;
            }
        }
        finally
        {
            // Unsubscribing is the point: without it a departed listener keeps
            // receiving forever and the list grows for the process lifetime.
            lock (_gate)
            {
                _ = _subscribers.Remove(queue);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var subscriber in _subscribers)
            {
                _ = subscriber.Writer.TryComplete();
            }

            _subscribers.Clear();
        }
    }
}
