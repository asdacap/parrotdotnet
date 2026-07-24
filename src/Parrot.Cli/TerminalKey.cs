namespace Parrot.Cli;

internal readonly record struct TerminalKey(TerminalKeyKind Kind, string Text)
{
    public TerminalKey(TerminalKeyKind kind)
        : this(kind, string.Empty)
    {
    }
}
