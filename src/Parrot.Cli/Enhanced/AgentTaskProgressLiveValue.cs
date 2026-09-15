using Google.Protobuf.Collections;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class AgentTaskProgressLiveValue(AgentTaskProgressSnapshot snapshot) : ILiveBufferItem
{
    public MultiLine Render(LiveBufferRenderContext context)
    {
        var columns = context.Decoration.ContentColumns(context.Columns);
        var rows = new List<string> { "Agent tasks:" };
        var remaining = new Stack<NodeFrame>();
        Push(snapshot.RootNodes, string.Empty, true, remaining);
        while (remaining.Count > 0)
        {
            var frame = remaining.Pop();
            var connector = frame.Root ? string.Empty : frame.Last ? "└── " : "├── ";
            var prefix = frame.Ancestors + connector;
            var lines = TerminalText.LayoutHanging(
                $"{prefix}{Icon(frame.Node.Status)} {AgentTaskProgressFormatter.DisplayText(frame.Node)}",
                columns,
                new string(' ', TerminalText.Width(prefix)));
            rows.AddRange(lines);
            var descendants = frame.Ancestors + (frame.Root ? string.Empty : frame.Last ? "    " : "│   ");
            Push(frame.Node.Children, descendants, false, remaining);
        }

        return new MultiLine(
            [.. context.Decoration.Apply(TerminalIcons.Activity, rows)
                .Select(row => new TerminalLine(row, context.Palette.LiveSurface))],
            null,
            LiveBufferRetention.Fixed);
    }

    private static void Push(
        RepeatedField<AgentTaskProgressNode> nodes,
        string ancestors,
        bool roots,
        Stack<NodeFrame> remaining)
    {
        for (var index = nodes.Count - 1; index >= 0; index--)
        {
            remaining.Push(new NodeFrame(nodes[index], ancestors, roots, index == nodes.Count - 1));
        }
    }

    private static string Icon(AgentTaskProgressStatus status) => status switch
    {
        AgentTaskProgressStatus.Pending => "○",
        AgentTaskProgressStatus.Running => "◐",
        AgentTaskProgressStatus.Succeeded => "✓",
        AgentTaskProgressStatus.Failed => "✗",
        AgentTaskProgressStatus.Blocked => "⊘",
        AgentTaskProgressStatus.Canceled => "■",
        _ => "?",
    };

    private readonly record struct NodeFrame(
        AgentTaskProgressNode Node,
        string Ancestors,
        bool Root,
        bool Last);
}
