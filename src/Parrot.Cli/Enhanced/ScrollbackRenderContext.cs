namespace Parrot.Cli.Enhanced;

internal readonly record struct ScrollbackRenderContext(int Columns, TerminalPalette Palette, bool InlineDiff)
{
    public ScrollbackRenderContext(int columns, TerminalPalette palette)
        : this(columns, palette, true)
    {
    }

    public ActivityDecoration Decoration { get; init; } = ActivityDecoration.None;

    /// <summary>Gets the packing identity of the previous adjacent item, or null when it has none.</summary>
    public object? PreviousPacking { get; init; }
}
