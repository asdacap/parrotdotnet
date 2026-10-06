using Parrot.Cli.Enhanced;
using Parrot.Protocol;

namespace Parrot.Cli;

internal readonly record struct AgentTaskRow(string Text, string HangingIndent)
{
    internal AgentTaskProgressStatus? Status { get; init; }

    internal LiveModelAliasIcon? ModelAliasIcon { get; init; }

    internal int GlyphStartIndex { get; init; }

    internal static AgentTaskRow Create(string lead, string description) =>
        new(lead + description, new string(' ', TerminalText.Width(lead)));
}
