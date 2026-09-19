using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class AgentTaskProgressLiveValue(AgentTaskProgressSnapshot snapshot) : ILiveBufferItem
{
    public MultiLine Render(LiveBufferRenderContext context)
    {
        var columns = context.Decoration.ContentColumns(context.Columns);
        var rows = new List<string>();
        foreach (var row in AgentTaskProgressFormatter.FormatRows(snapshot))
        {
            rows.AddRange(TerminalText.LayoutHanging(row.Text, columns, row.HangingIndent));
        }

        return new MultiLine(
            [.. context.Decoration.Apply(TerminalIcons.Activity, rows)
                .Select(row => new TerminalLine(row, context.Palette.LiveSurface))],
            null,
            LiveBufferRetention.Fixed);
    }
}
