namespace Parrot.Cli.Enhanced;

internal sealed class TerminalPalette(bool color)
{
    public TerminalStyle LiveBackground { get; } = new(color ? "\u001b[48;5;236m" : string.Empty);

    public TerminalStyle LiveSurface { get; } = new(color ? "\u001b[48;5;236m\u001b[38;5;252m" : string.Empty);

    public TerminalStyle Muted { get; } = new(color ? "\u001b[38;5;245m" : string.Empty);

    public TerminalStyle Prompt { get; } = new(color ? "\u001b[48;5;236m\u001b[32m" : string.Empty);

    public TerminalStyle Marker { get; } = new(color ? "\u001b[48;5;236m\u001b[36m" : string.Empty);

    public TerminalStyle Selection { get; } = new(color ? "\u001b[48;5;240m\u001b[38;5;231m" : string.Empty);

    public TerminalStyle User { get; } = new(color ? "\u001b[38;5;230m" : string.Empty);

    public TerminalStyle Assistant { get; } = new(color ? "\u001b[38;5;195m" : string.Empty);

    public TerminalStyle Modeline { get; } = new(color ? "\u001b[48;5;236m\u001b[32m" : string.Empty);

    public TerminalStyle Success { get; } = new(color ? "\u001b[32m" : string.Empty);

    public TerminalStyle Failure { get; } = new(color ? "\u001b[31m" : string.Empty);
}
