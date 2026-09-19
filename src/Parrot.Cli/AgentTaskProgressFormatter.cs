using Google.Protobuf.Collections;
using Parrot.Cli.Enhanced;
using Parrot.Protocol;

namespace Parrot.Cli;

internal static class AgentTaskProgressFormatter
{
    public static IReadOnlyList<string> Format(AgentTaskProgressSnapshot snapshot) =>
        [.. FormatRows(snapshot).Select(static row => row.Text)];

    internal static IReadOnlyList<AgentTaskRow> FormatRows(AgentTaskProgressSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var rows = new List<AgentTaskRow> { new("Agent tasks:", string.Empty) };
        for (var index = 0; index < snapshot.RootNodes.Count; index++)
        {
            var node = snapshot.RootNodes[index];
            rows.Add(AgentTaskRow.Create($"{Icon(node.Status)} ", DisplayText(node)));
            Append(node.Children, string.Empty, rows);
        }

        return rows;
    }

    internal static string DisplayText(AgentTaskProgressNode node)
    {
        var text = string.IsNullOrEmpty(node.Description) ? node.Name : node.Description;
        return TerminalText.Sanitize(text).Replace('\n', ' ');
    }

    private static void Append(
        RepeatedField<AgentTaskProgressNode> nodes,
        string ancestors,
        List<AgentTaskRow> rows)
    {
        for (var index = 0; index < nodes.Count; index++)
        {
            var last = index == nodes.Count - 1;
            var node = nodes[index];
            var lead = $"{ancestors}{(last ? "└──" : "├──")} {Icon(node.Status)} ";
            rows.Add(AgentTaskRow.Create(lead, DisplayText(node)));
            Append(node.Children, ancestors + (last ? "    " : "│   "), rows);
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
}
