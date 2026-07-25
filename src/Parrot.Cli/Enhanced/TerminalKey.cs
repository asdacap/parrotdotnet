namespace Parrot.Cli.Enhanced;

internal readonly record struct TerminalKey(TerminalKeyKind Kind, string Text)
{
    public TerminalKey(TerminalKeyKind kind)
        : this(kind, string.Empty)
    {
    }
}
