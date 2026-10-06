using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class AgentTaskProgressScrollbackValue(AgentTaskProgressSnapshot snapshot) : IScrollbackItem
{
    public bool IsCompleted => true;

    public ScrollbackLayout Layout => ScrollbackLayout.Block;

    public bool Continues(IScrollbackItem previous) => false;

    public IReadOnlyList<string> Render(ScrollbackRenderContext context)
    {
        var columns = context.Decoration.ContentColumns(context.Columns);
        var result = new List<string>();
        var styles = new List<TerminalStyle>();
        foreach (var row in AgentTaskProgressFormatter.FormatRows(snapshot, null))
        {
            var wrapped = TerminalText.LayoutWordsHanging(row.Text, columns, row.HangingIndent);
            result.AddRange(wrapped);
            styles.AddRange(Enumerable.Repeat(context.Palette.GetTaskStyle(row.Status, false), wrapped.Count));
        }

        return [.. context.Decoration.Apply(TerminalIcons.Activity, result)
            .Select((line, index) => styles[index].Apply(line))];
    }
}
