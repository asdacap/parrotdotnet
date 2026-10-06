namespace Parrot.Cli.Enhanced;

internal sealed record TaskAgentLine(string Text, LiveModelAliasIcon? ModelAliasIcon)
{
    public int GlyphStartIndex { get; init; }
}
