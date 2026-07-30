using Parrot.Llm;

namespace Parrot.Cli.Enhanced;

internal sealed class TerminalPalette(bool color)
{
    public bool ColorEnabled { get; } = color;

    public TerminalStyle LiveBackground { get; } = new(color ? "\u001b[48;5;236m" : string.Empty);

    public TerminalStyle LiveSurface { get; } = new(color ? "\u001b[48;5;236m\u001b[38;5;252m" : string.Empty);

    public TerminalStyle LiveMuted { get; } = new(color ? "\u001b[48;5;236m\u001b[38;5;245m" : string.Empty);

    public TerminalStyle Muted { get; } = new(color ? "\u001b[38;5;245m" : string.Empty);

    public TerminalStyle Prompt { get; } = new(color ? "\u001b[48;5;236m\u001b[32m" : string.Empty);

    public TerminalStyle Marker { get; } = new(color ? "\u001b[48;5;236m\u001b[36m" : string.Empty);

    public TerminalStyle Selection { get; } = new(color ? "\u001b[48;5;240m\u001b[38;5;231m" : string.Empty);

    public TerminalStyle UserMessage { get; } = new(color ? "\u001b[32m" : string.Empty);

    public TerminalStyle AssistantMessage { get; } = new(color ? "\u001b[38;5;195m" : string.Empty);

    public TerminalStyle User => UserMessage;

    public TerminalStyle Assistant => AssistantMessage;

    public TerminalStyle Modeline { get; } = new(color ? "\u001b[48;5;236m\u001b[32m" : string.Empty);

    public TerminalStyle Success { get; } = new(color ? "\u001b[32m" : string.Empty);

    public TerminalStyle Failure { get; } = new(color ? "\u001b[31m" : string.Empty);

    public TerminalStyle DiffAdded => Success;

    public TerminalStyle DiffRemoved => Failure;

    public TerminalStyle GetLiveIconStyle(ModelAliasIconColor iconColor)
    {
        if (!ColorEnabled)
        {
            return default;
        }

        var foreground = iconColor switch
        {
            ModelAliasIconColor.Black => 30,
            ModelAliasIconColor.Red => 31,
            ModelAliasIconColor.Green => 32,
            ModelAliasIconColor.Yellow => 33,
            ModelAliasIconColor.Blue => 34,
            ModelAliasIconColor.Magenta => 35,
            ModelAliasIconColor.Cyan => 36,
            ModelAliasIconColor.White => 37,
            _ => throw new ArgumentOutOfRangeException(nameof(iconColor), iconColor, null),
        };
        return new TerminalStyle($"\u001b[48;5;236m\u001b[{foreground}m");
    }
}
