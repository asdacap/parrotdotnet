using System.Threading.Channels;
using Parrot.Protocol;

namespace Parrot.Events;

internal sealed class EventSubscription(EventBroker owner, Channel<Event> channel) : IDisposable
{
    private bool _disposed;

    public ChannelReader<Event> Reader => channel.Reader;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        owner.Unsubscribe(channel);
    }
}
