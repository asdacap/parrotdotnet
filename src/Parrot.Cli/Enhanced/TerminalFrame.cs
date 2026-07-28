namespace Parrot.Cli.Enhanced;

internal readonly record struct TerminalFrame(
    IReadOnlyList<TerminalLine> Lines,
    LiveBufferCaret Caret);
