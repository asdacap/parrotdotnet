using Google.Protobuf.Collections;
using Parrot.Cli.Enhanced;
using Parrot.Protocol;

namespace Parrot.Cli;

internal static class AgentTaskProgressFormatter
{
    public static IReadOnlyList<string> Format(AgentTaskProgressSnapshot snapshot) =>
        [.. FormatRows(snapshot, null).Select(static row => row.Text)];

    /// <summary>
    /// Formats the tree. A running node whose agent has a line in <paramref name="agentLines"/> shows that line in
    /// place of its status icon, with the task description beneath it.
    /// </summary>
    internal static IReadOnlyList<AgentTaskRow> FormatRows(
        AgentTaskProgressSnapshot snapshot,
        IReadOnlyDictionary<string, TaskAgentLine>? agentLines)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var rows = new List<AgentTaskRow> { new("Agent tasks:", string.Empty) };
        for (var index = 0; index < snapshot.RootNodes.Count; index++)
        {
            var node = snapshot.RootNodes[index];
            AppendNode(node, string.Empty, string.Empty, agentLines, rows);
            Append(node.Children, string.Empty, agentLines, rows);
        }

        return rows;
    }

    /// <summary>
    /// Copies the tree with the task graph of each node's child agent, when <paramref name="trees"/> has one, nested as
    /// that node's children.
    /// </summary>
    internal static AgentTaskProgressSnapshot Nest(
        AgentTaskProgressSnapshot snapshot,
        IReadOnlyDictionary<string, AgentTaskProgressSnapshot> trees)
    {
        var nested = snapshot.Clone();
        NestNodes(nested.RootNodes, trees, new HashSet<string>(StringComparer.Ordinal));
        return nested;
    }

    internal static IEnumerable<string> RunningAgentSessionIds(AgentTaskProgressSnapshot snapshot) =>
        snapshot.RootNodes.SelectMany(Flatten)
            .Where(static node => node.Status == AgentTaskProgressStatus.Running && node.AgentSessionId.Length > 0)
            .Select(static node => node.AgentSessionId);

    internal static string DisplayText(AgentTaskProgressNode node)
    {
        var text = string.IsNullOrEmpty(node.Description) ? node.Name : node.Description;
        return TerminalText.Sanitize(text).Replace('\n', ' ');
    }

    private static void Append(
        RepeatedField<AgentTaskProgressNode> nodes,
        string ancestors,
        IReadOnlyDictionary<string, TaskAgentLine>? agentLines,
        List<AgentTaskRow> rows)
    {
        for (var index = 0; index < nodes.Count; index++)
        {
            var last = index == nodes.Count - 1;
            var node = nodes[index];
            var descendants = ancestors + (last ? "    " : "│   ");
            AppendNode(node, $"{ancestors}{(last ? "└──" : "├──")} ", descendants, agentLines, rows);
            Append(node.Children, descendants, agentLines, rows);
        }
    }

    private static void AppendNode(
        AgentTaskProgressNode node,
        string lead,
        string continuation,
        IReadOnlyDictionary<string, TaskAgentLine>? agentLines,
        List<AgentTaskRow> rows)
    {
        if (node.Status == AgentTaskProgressStatus.Running
            && agentLines is not null
            && agentLines.TryGetValue(node.AgentSessionId, out var agentLine))
        {
            rows.Add(AgentTaskRow.Create(lead, agentLine.Text) with { Status = node.Status, ModelAliasIcon = agentLine.ModelAliasIcon });
            rows.Add(AgentTaskRow.Create(continuation + "  ", DisplayText(node)) with { Status = node.Status });
            return;
        }

        rows.Add(AgentTaskRow.Create($"{lead}{Icon(node.Status)} ", DisplayText(node)) with { Status = node.Status });
    }

    private static void NestNodes(
        RepeatedField<AgentTaskProgressNode> nodes,
        IReadOnlyDictionary<string, AgentTaskProgressSnapshot> trees,
        HashSet<string> ancestors)
    {
        foreach (var node in nodes)
        {
            if (!trees.TryGetValue(node.AgentSessionId, out var tree) || !ancestors.Add(node.AgentSessionId))
            {
                continue;
            }

            node.Children.Clear();
            node.Children.Add(tree.RootNodes.Select(static child => child.Clone()));
            NestNodes(node.Children, trees, ancestors);
            _ = ancestors.Remove(node.AgentSessionId);
        }
    }

    private static IEnumerable<AgentTaskProgressNode> Flatten(AgentTaskProgressNode node) =>
        node.Children.SelectMany(Flatten).Prepend(node);

    private static string Icon(AgentTaskProgressStatus status) => status switch
    {
        AgentTaskProgressStatus.Pending => "○",
        AgentTaskProgressStatus.Running => "◐",
        AgentTaskProgressStatus.Succeeded => "✓",
        AgentTaskProgressStatus.Failed => "✗",
        AgentTaskProgressStatus.Canceled => "■",
        _ => "?",
    };
}
