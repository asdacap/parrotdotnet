namespace Parrot.Cli.Enhanced;

internal readonly record struct LiveBufferRenderContext(int Columns, TerminalPalette Palette)
{
    public string ActivityOwner { get; init; } = string.Empty;
}
