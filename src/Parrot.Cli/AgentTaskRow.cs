using Parrot.Cli.Enhanced;

namespace Parrot.Cli;

internal readonly record struct AgentTaskRow(string Text, string HangingIndent)
{
    internal static AgentTaskRow Create(string lead, string description) =>
        new(lead + description, new string(' ', TerminalText.Width(lead)));
}
