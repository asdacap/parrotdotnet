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

    public bool Continues(IScrollbackItem previous) =>
        previous is StreamingScrollbackValue value && ReferenceEquals(_sequence, value._sequence);

    public IReadOnlyList<string> Render(ScrollbackRenderContext context) => lines;
}
