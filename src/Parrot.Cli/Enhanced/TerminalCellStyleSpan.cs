namespace Parrot.Cli.Enhanced;

internal readonly record struct TerminalCellStyleSpan(int StartCell, int Length, TerminalStyle Style);
