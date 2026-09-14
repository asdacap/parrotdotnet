namespace Parrot.Events;

/// <summary>Publishes one inventory's snapshots onto the session event stream.</summary>
internal interface IInventoryPublisher
{
    /// <summary>Captures the current inventory as replayable events without subscribing.</summary>
    IReadOnlyList<Protocol.Event> CaptureSnapshotEvents();

    /// <summary>Publishes each later inventory snapshot until the inventory closes.</summary>
    Task Run();
}
