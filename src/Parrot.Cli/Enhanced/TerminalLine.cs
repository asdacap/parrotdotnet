namespace Parrot.Cli.Enhanced;

internal readonly struct TerminalLine : IEquatable<TerminalLine>
{
    public TerminalLine(string text, TerminalStyle style)
        : this(text, style, [])
    {
    }

    public TerminalLine(string text, TerminalStyle style, IReadOnlyList<TerminalCellStyleSpan> styleSpans)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(styleSpans);
        Text = text;
        Style = style;
        StyleSpanValues = [.. styleSpans];
    }

    public string Text { get; }

    public TerminalStyle Style { get; }

    public IReadOnlyList<TerminalCellStyleSpan> StyleSpans => StyleSpanValues ?? [];

    private IReadOnlyList<TerminalCellStyleSpan>? StyleSpanValues { get; }

    public static bool operator ==(TerminalLine left, TerminalLine right) => left.Equals(right);

    public static bool operator !=(TerminalLine left, TerminalLine right) => !left.Equals(right);

    public bool Equals(TerminalLine other) =>
        string.Equals(Text, other.Text, StringComparison.Ordinal)
        && Style == other.Style
        && StyleSpans.SequenceEqual(other.StyleSpans);

    public override bool Equals(object? obj) => obj is TerminalLine other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(Text, StringComparer.Ordinal);
        hash.Add(Style);
        foreach (var span in StyleSpans)
        {
            hash.Add(span);
        }

        return hash.ToHashCode();
    }
}
