using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class AgentTaskProgressScrollbackValue(AgentTaskProgressSnapshot snapshot) : IScrollbackItem
{
    public bool IsCompleted => true;

    public ScrollbackLayout Layout => ScrollbackLayout.Block;

    public bool Continues(IScrollbackItem previous) => false;

    public IReadOnlyList<string> Render(ScrollbackRenderContext context)
    {
        var result = new List<string>();
        foreach (var line in AgentTaskProgressFormatter.Format(snapshot))
        {
            var connector = line.IndexOf("─ ", StringComparison.Ordinal);
            var indent = connector < 0 ? string.Empty : new string(' ', TerminalText.Width(line[..(connector + 2)]));
            result.AddRange(TerminalText.LayoutHanging(line, Math.Max(1, context.Columns), indent));
        }

        return result;
    }
}
