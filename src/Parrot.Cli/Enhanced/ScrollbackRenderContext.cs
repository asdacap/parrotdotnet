namespace Parrot.Cli.Enhanced;

internal readonly record struct ScrollbackRenderContext(int Columns, TerminalPalette Palette, bool InlineDiff)
{
    public ScrollbackRenderContext(int columns, TerminalPalette palette)
        : this(columns, palette, true)
    {
    }

    public ActivityDecoration Decoration { get; init; } = ActivityDecoration.None;
}
