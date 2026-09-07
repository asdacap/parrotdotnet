namespace Parrot.Cli.Enhanced;

internal sealed class StreamingScrollbackValue(
    StreamingScrollbackSequenceValue sequence,
    IReadOnlyList<string> lines,
    bool completed,
    bool startsLayout,
    bool endsLayout,
    ScrollbackLayout layout) : IScrollbackItem
{
    private readonly StreamingScrollbackSequenceValue _sequence = sequence;

    public bool IsCompleted => completed;

    public ScrollbackLayout Layout => layout;

    public bool StartsLayout => startsLayout;

    public bool EndsLayout => endsLayout;

    public object SequenceIdentity => _sequence;

    public bool Continues(IScrollbackItem previous) =>
        ReferenceEquals(_sequence, previous.SequenceIdentity);

    public IReadOnlyList<string> Render(ScrollbackRenderContext context) => lines;
}
