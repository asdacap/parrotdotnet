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
        foreach (var row in AgentTaskProgressFormatter.FormatRows(snapshot))
        {
            result.AddRange(TerminalText.LayoutWordsHanging(row.Text, columns, row.HangingIndent));
        }

        return context.Decoration.Apply(TerminalIcons.Activity, result);
    }
}
