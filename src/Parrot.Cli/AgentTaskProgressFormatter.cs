using Google.Protobuf.Collections;
using Parrot.Cli.Enhanced;
using Parrot.Protocol;

namespace Parrot.Cli;

internal static class AgentTaskProgressFormatter
{
    public static IReadOnlyList<string> Format(AgentTaskProgressSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var lines = new List<string> { "Agent tasks:" };
        for (var index = 0; index < snapshot.RootNodes.Count; index++)
        {
            var node = snapshot.RootNodes[index];
            lines.Add($"{Icon(node.Status)} {DisplayText(node)}");
            Append(node.Children, string.Empty, lines);
        }

        return lines;
    }

    internal static string DisplayText(AgentTaskProgressNode node)
    {
        var text = string.IsNullOrEmpty(node.Description) ? node.Name : node.Description;
        return TerminalText.Sanitize(text).Replace('\n', ' ');
    }

    private static void Append(
        RepeatedField<AgentTaskProgressNode> nodes,
        string ancestors,
        List<string> lines)
    {
        for (var index = 0; index < nodes.Count; index++)
        {
            var last = index == nodes.Count - 1;
            var node = nodes[index];
            lines.Add($"{ancestors}{(last ? "└──" : "├──")} {Icon(node.Status)} {DisplayText(node)}");
            Append(node.Children, ancestors + (last ? "    " : "│   "), lines);
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
