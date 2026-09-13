using System.Threading.Channels;
using Parrot.Protocol;

namespace Parrot.Events;

internal sealed class EventSubscription : IEventSubscription
{
    private const int CapacityPerRetention = 1024;

    private readonly IEventBroker _owner;
    private readonly Lock _gate = new();
    private readonly LinkedList<BufferedEvent> _events = [];
    private readonly LinkedList<IReadOnlyList<Event>> _inventories = [];
    private readonly Channel<bool> _ready = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyList<Event> _inventoryChunks = [];
    private int _inventoryChunkIndex;
    private int _retainedCount;
    private int _transientCount;
    private bool _preferEvent;
    private bool _completed;
    private bool _disposed;

    public EventSubscription(IEventBroker owner)
    {
        _owner = owner;
        Reader = new SubscriptionReader(this);
    }

    public ChannelReader<Event> Reader { get; }

    private bool HasPending => _events.Count > 0 || _inventories.Count > 0 || _inventoryChunkIndex < _inventoryChunks.Count;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _owner.Unsubscribe(this);
    }

    public void Publish(Event published, bool transient)
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            if (transient)
            {
                if (_transientCount == CapacityPerRetention)
                {
                    RemoveOldest(transient: true);
                }

                _transientCount++;
            }
            else
            {
                if (_retainedCount == CapacityPerRetention)
                {
                    RemoveOldest(transient: false);
                }

                _retainedCount++;
            }

            _ = _events.AddLast(new BufferedEvent(published, transient));
            _ = _ready.Writer.TryWrite(true);
        }
    }

    public void PublishQueueSnapshot(IReadOnlyList<Event> chunks)
    {
        ValidateQueueSnapshotBatch(chunks);
        PublishInventory(chunks);
    }

    public void PublishProcessSnapshot(IReadOnlyList<Event> chunks)
    {
        ValidateProcessSnapshotBatch(chunks);
        PublishInventory(chunks);
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _ = _ready.Writer.TryComplete();
            CompleteReadingWhenDrained();
        }
    }

    internal static void ValidateQueueSnapshotBatch(IReadOnlyList<Event> chunks) =>
        ValidateSnapshotBatch(chunks, Event.PayloadOneofCase.QueueSnapshot);

    internal static void ValidateProcessSnapshotBatch(IReadOnlyList<Event> chunks) =>
        ValidateSnapshotBatch(chunks, Event.PayloadOneofCase.ShellProcessSnapshot);

    private static void ValidateSnapshotBatch(IReadOnlyList<Event> chunks, Event.PayloadOneofCase expectedPayload)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        foreach (var chunk in chunks)
        {
            if (chunk is null || chunk.PayloadCase != expectedPayload)
            {
                throw new InvalidOperationException($"Expected a complete {expectedPayload} snapshot batch.");
            }
        }
    }

    private static bool SameInventory(Event first, Event second) =>
        first.PayloadCase == second.PayloadCase && first.PayloadCase switch
        {
            Event.PayloadOneofCase.QueueSnapshot => string.Equals(
                first.QueueSnapshot.OwnerAgentSessionId, second.QueueSnapshot.OwnerAgentSessionId, StringComparison.Ordinal),
            Event.PayloadOneofCase.ShellProcessSnapshot => string.Equals(
                first.ShellProcessSnapshot.OwnerAgentSessionId, second.ShellProcessSnapshot.OwnerAgentSessionId, StringComparison.Ordinal),
            _ => throw new InvalidOperationException("Expected an inventory snapshot."),
        };

    private void PublishInventory(IReadOnlyList<Event> chunks)
    {
        if (chunks.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            for (var node = _inventories.First; node is not null; node = node.Next)
            {
                if (SameInventory(node.Value[0], chunks[0]))
                {
                    _inventories.Remove(node);
                    break;
                }
            }

            _ = _inventories.AddLast(chunks);
            _ = _ready.Writer.TryWrite(true);
        }
    }

    private void RemoveOldest(bool transient)
    {
        for (var node = _events.First; node is not null; node = node.Next)
        {
            if (node.Value.Transient != transient)
            {
                continue;
            }

            _events.Remove(node);
            if (transient)
            {
                _transientCount--;
            }
            else
            {
                _retainedCount--;
            }

            return;
        }
    }

    private bool TryRead(out Event published)
    {
        lock (_gate)
        {
            if (_inventoryChunkIndex == _inventoryChunks.Count
                && (!_preferEvent || _events.Count == 0)
                && _inventories.First is { } inventory)
            {
                _inventoryChunks = inventory.Value;
                _inventoryChunkIndex = 0;
                _inventories.RemoveFirst();
            }

            if (_inventoryChunkIndex < _inventoryChunks.Count)
            {
                published = _inventoryChunks[_inventoryChunkIndex++];
                _preferEvent = _inventoryChunkIndex == _inventoryChunks.Count;
                if (HasPending)
                {
                    _ = _ready.Writer.TryWrite(true);
                }

                CompleteReadingWhenDrained();
                return true;
            }

            if (_events.First is not { } first)
            {
                published = new Event();
                CompleteReadingWhenDrained();
                return false;
            }

            _events.RemoveFirst();
            _preferEvent = false;
            published = first.Value.Published;
            if (first.Value.Transient)
            {
                _transientCount--;
            }
            else
            {
                _retainedCount--;
            }

            if (HasPending)
            {
                _ = _ready.Writer.TryWrite(true);
            }

            CompleteReadingWhenDrained();
            return true;
        }
    }

    private ValueTask<bool> WaitToRead(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (HasPending)
            {
                return ValueTask.FromResult(true);
            }

            if (_completed)
            {
                return ValueTask.FromResult(false);
            }
        }

        return WaitForSignal(cancellationToken);
    }

    private async ValueTask<bool> WaitForSignal(CancellationToken cancellationToken)
    {
        while (await _ready.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_ready.Reader.TryRead(out _))
            {
            }

            lock (_gate)
            {
                if (HasPending)
                {
                    return true;
                }

                if (_completed)
                {
                    return false;
                }
            }
        }

        return false;
    }

    private void CompleteReadingWhenDrained()
    {
        if (_completed && !HasPending)
        {
            _ = _completion.TrySetResult();
        }
    }

    private readonly record struct BufferedEvent(Event Published, bool Transient);

    private sealed class SubscriptionReader(EventSubscription owner) : ChannelReader<Event>
    {
        public override Task Completion => owner._completion.Task;

        public override bool TryRead(out Event item) => owner.TryRead(out item);

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) =>
            owner.WaitToRead(cancellationToken);
    }
}
