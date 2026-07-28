namespace Parrot.Cli.Enhanced;

internal readonly record struct MultiLine(
    IReadOnlyList<TerminalLine> Lines,
    LiveBufferCaret? Caret,
    LiveBufferRetention Retention);
