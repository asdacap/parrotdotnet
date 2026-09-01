using System.Threading.Channels;
using Parrot.Protocol;

namespace Parrot.Events;

internal sealed class EventSubscription : IDisposable
{
    private const int CapacityPerRetention = 1024;

    private readonly EventBroker _owner;
    private readonly Lock _gate = new();
    private readonly LinkedList<BufferedEvent> _events = [];
    private readonly Channel<bool> _ready = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _retainedCount;
    private int _transientCount;
    private bool _completed;
    private bool _disposed;

    public EventSubscription(EventBroker owner)
    {
        _owner = owner;
        Reader = new SubscriptionReader(this);
    }

    public ChannelReader<Event> Reader { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _owner.Unsubscribe(this);
    }

    internal void Publish(Event published, bool transient)
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

    internal void Complete()
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
            if (_events.First is not { } first)
            {
                published = new Event();
                CompleteReadingWhenDrained();
                return false;
            }

            _events.RemoveFirst();
            published = first.Value.Published;
            if (first.Value.Transient)
            {
                _transientCount--;
            }
            else
            {
                _retainedCount--;
            }

            if (_events.Count > 0)
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
            if (_events.Count > 0)
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
                if (_events.Count > 0)
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
        if (_completed && _events.Count == 0)
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
