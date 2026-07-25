namespace Parrot.Cli.Enhanced;

internal readonly record struct TerminalStyle(string Start)
{
    public const string Reset = "\u001b[0m";

    public string Apply(string value) => Start.Length == 0 ? value : $"{Start}{value}{Reset}";
}
