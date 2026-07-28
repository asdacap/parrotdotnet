namespace Parrot.Cli.Enhanced;

internal sealed class StreamingScrollbackSequenceValue
{
    private bool _started;

    public IScrollbackItem Append(IReadOnlyList<string> lines)
    {
        var starts = !_started && lines.Count > 0;
        _started |= lines.Count > 0;
        return new StreamingScrollbackValue(this, lines, false, starts, false, ScrollbackLayout.Assistant);
    }

    public IScrollbackItem Complete(IReadOnlyList<string> lines)
    {
        var starts = !_started && lines.Count > 0;
        var layout = _started || lines.Count > 0 ? ScrollbackLayout.Assistant : ScrollbackLayout.Compact;
        _started = false;
        return new StreamingScrollbackValue(this, lines, true, starts, layout == ScrollbackLayout.Assistant, layout);
    }
}
