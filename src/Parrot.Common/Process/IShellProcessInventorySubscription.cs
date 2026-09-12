using System.Threading.Channels;

namespace Parrot.Process;

/// <summary>Owns a process inventory subscription; disposal stops delivery and completes its reader.</summary>
internal interface IShellProcessInventorySubscription : IDisposable
{
    ChannelReader<ShellProcessInventorySnapshot> Reader { get; }
}
