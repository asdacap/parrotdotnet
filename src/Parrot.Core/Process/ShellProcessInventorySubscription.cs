using System.Threading.Channels;

namespace Parrot.Process;

internal sealed class ShellProcessInventorySubscription(
    ShellProcessInventoryFeed owner,
    Channel<ShellProcessInventorySnapshot> channel) : IDisposable
{
    private bool _disposed;

    public ChannelReader<ShellProcessInventorySnapshot> Reader => channel.Reader;

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
