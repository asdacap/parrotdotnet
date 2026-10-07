using System.Diagnostics;
using System.Text;
using Parrot.Protocol;

namespace Parrot.Agent;

// Text and reasoning stream a few tokens at a time, and every event is a
// commit. The fragment opening a stream commits at once; the rest of that
// stream is merged into one event, held no longer than the hold interval.
internal sealed class StreamedFragmentBuffer
{
    private static readonly TimeSpan HoldInterval = TimeSpan.FromMilliseconds(500);

    private readonly StringBuilder _fragment = new();
    private FragmentStream? _stream;
    private Event? _held;
    private long _heldSince;

    public bool IsHolding => _held is not null;

    public TimeSpan RemainingHold => _held is null
        ? TimeSpan.Zero
        : TimeSpan.FromTicks(Math.Max(0, (HoldInterval - Stopwatch.GetElapsedTime(_heldSince)).Ticks));

    // The events to commit now, in order.
    public IReadOnlyList<Event> Add(Event published)
    {
        ArgumentNullException.ThrowIfNull(published);
        var stream = FragmentStream.Of(published);
        if (stream is null || stream != _stream)
        {
            var held = EndStream();
            _stream = stream?.Close(published);
            return held is null ? [published] : [held, published];
        }

        if (_held is null)
        {
            _held = published;
            _heldSince = Stopwatch.GetTimestamp();
        }
        else if (published.PayloadCase == Event.PayloadOneofCase.ReasoningChunk)
        {
            _held.ReasoningChunk.Completed = published.ReasoningChunk.Completed;
        }

        _ = _fragment.Append(FragmentStream.Fragment(published));
        _stream = stream.Close(published);
        return (_stream is null || Stopwatch.GetElapsedTime(_heldSince) >= HoldInterval) && Take() is { } ready
            ? [ready]
            : [];
    }

    // Commits what is held without ending the stream, so later fragments keep merging.
    public Event? Take()
    {
        var held = _held;
        if (held is null)
        {
            return null;
        }

        if (held.PayloadCase == Event.PayloadOneofCase.TextChunk)
        {
            held.TextChunk.Fragment = _fragment.ToString();
        }
        else
        {
            held.ReasoningChunk.Fragment = _fragment.ToString();
        }

        _held = null;
        _ = _fragment.Clear();
        return held;
    }

    public Event? EndStream()
    {
        _stream = null;
        return Take();
    }

    private sealed record FragmentStream(Event.PayloadOneofCase Payload, ReasoningKind Kind, string PartId)
    {
        public static FragmentStream? Of(Event published) => published.PayloadCase switch
        {
            Event.PayloadOneofCase.TextChunk => new(published.PayloadCase, ReasoningKind.Raw, string.Empty),
            Event.PayloadOneofCase.ReasoningChunk => new(published.PayloadCase, published.ReasoningChunk.Kind, published.ReasoningChunk.PartId),
            _ => null,
        };

        public static string Fragment(Event published) => published.PayloadCase == Event.PayloadOneofCase.TextChunk
            ? published.TextChunk.Fragment
            : published.ReasoningChunk.Fragment;

        // A completed reasoning part ends its stream.
        public FragmentStream? Close(Event published) =>
            published.PayloadCase == Event.PayloadOneofCase.ReasoningChunk && published.ReasoningChunk.Completed ? null : this;
    }
}
