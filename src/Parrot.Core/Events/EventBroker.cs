using System.Threading.Channels;
using Parrot.Protocol;

namespace Parrot.Events;

// Serialised publication over a bounded channel (principle 9). A channel rather
// than a .NET event: a multicast delegate has neither ordering nor backpressure.
//
// M1 only: with no EventRepository yet, this publishes directly rather than
// only after a durable commit. M2 closes that, and components.md records it.
internal sealed class EventBroker
{
    private readonly Channel<Event> _events =
        Channel.CreateBounded<Event>(new BoundedChannelOptions(1024) { SingleReader = true });

    public ValueTask Publish(Event published, CancellationToken cancellationToken) =>
        _events.Writer.WriteAsync(published, cancellationToken);

    public void Complete() => _events.Writer.TryComplete();

    public IAsyncEnumerable<Event> Subscribe(CancellationToken cancellationToken) =>
        _events.Reader.ReadAllAsync(cancellationToken);
}
