using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class AgentTaskProgressLiveValue(
    AgentTaskProgressSnapshot snapshot,
    IReadOnlyDictionary<string, TaskAgentLine>? agentLines) : ILiveBufferItem
{
    public AgentTaskProgressSnapshot Snapshot => snapshot;

    public MultiLine Render(LiveBufferRenderContext context)
    {
        var columns = context.Decoration.ContentColumns(context.Columns);
        var rows = new List<string>();
        var styles = new List<TerminalStyle>();
        foreach (var row in AgentTaskProgressFormatter.FormatRows(snapshot, agentLines))
        {
            var wrapped = TerminalText.LayoutWordsHanging(row.Text, columns, row.HangingIndent);
            var style = row.ModelAliasIcon is { } icon
                ? context.Palette.GetLiveIconStyle(icon.Color)
                : context.Palette.GetTaskStyle(row.Status, true);
            rows.AddRange(wrapped);
            styles.AddRange(Enumerable.Repeat(style, wrapped.Count));
        }

        return new MultiLine(
            [.. context.Decoration.Apply(TerminalIcons.Activity, rows)
                .Select((row, index) => new TerminalLine(row, styles[index]))],
            null,
            LiveBufferRetention.Fixed);
    }
}
