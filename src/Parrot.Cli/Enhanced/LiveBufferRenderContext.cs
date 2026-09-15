namespace Parrot.Cli.Enhanced;

internal readonly record struct LiveBufferRenderContext(int Columns, TerminalPalette Palette)
{
    public ActivityDecoration Decoration { get; init; } = ActivityDecoration.None;
}
