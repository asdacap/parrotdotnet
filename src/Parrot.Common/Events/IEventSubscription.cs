using System.Threading.Channels;
using Parrot.Protocol;

namespace Parrot.Events;

/// <summary>Owns an event stream subscription until it is disposed.</summary>
internal interface IEventSubscription : IDisposable
{
    ChannelReader<Event> Reader { get; }

    /// <summary>Buffers an event published by the owning broker.</summary>
    void Publish(Event published, bool transient);

    /// <summary>Replaces buffered queue snapshots with a complete set of chunks from the owning broker.</summary>
    void PublishQueueSnapshot(IReadOnlyList<Event> chunks);

    /// <summary>Replaces buffered process snapshots with a complete set of chunks from the owning broker.</summary>
    void PublishProcessSnapshot(IReadOnlyList<Event> chunks);

    /// <summary>Completes the stream when the owning broker removes or closes this subscription.</summary>
    void Complete();
}
