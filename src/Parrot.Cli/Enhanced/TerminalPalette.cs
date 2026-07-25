namespace Parrot.Cli.Enhanced;

internal sealed class TerminalPalette(bool color)
{
    public TerminalStyle LiveBackground { get; } = Style("\u001b[48;5;236m", color);

    public TerminalStyle LiveSurface { get; } = Style("\u001b[48;5;236m\u001b[38;5;252m", color);

    public TerminalStyle Muted { get; } = Style("\u001b[38;5;245m", color);

    public TerminalStyle Prompt { get; } = Style("\u001b[48;5;236m\u001b[32m", color);

    public TerminalStyle Marker { get; } = Style("\u001b[48;5;236m\u001b[36m", color);

    public TerminalStyle User { get; } = Style("\u001b[38;5;230m", color);

    public TerminalStyle Assistant { get; } = Style("\u001b[38;5;195m", color);

    public TerminalStyle Modeline { get; } = Style("\u001b[48;5;236m\u001b[32m", color);

    public TerminalStyle Success { get; } = Style("\u001b[32m", color);

    public TerminalStyle Failure { get; } = Style("\u001b[31m", color);

    private static TerminalStyle Style(string ansi, bool color) => new(color ? ansi : string.Empty);
}
