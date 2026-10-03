using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class AgentTaskProgressLiveValue(
    AgentTaskProgressSnapshot snapshot,
    IReadOnlyDictionary<string, string>? agentLines) : ILiveBufferItem
{
    public AgentTaskProgressSnapshot Snapshot => snapshot;

    public MultiLine Render(LiveBufferRenderContext context)
    {
        var columns = context.Decoration.ContentColumns(context.Columns);
        var rows = new List<string>();
        foreach (var row in AgentTaskProgressFormatter.FormatRows(snapshot, agentLines))
        {
            rows.AddRange(TerminalText.LayoutWordsHanging(row.Text, columns, row.HangingIndent));
        }

        return new MultiLine(
            [.. context.Decoration.Apply(TerminalIcons.Activity, rows)
                .Select(row => new TerminalLine(row, context.Palette.LiveSurface))],
            null,
            LiveBufferRetention.Fixed);
    }
}
