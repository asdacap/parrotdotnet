using Google.Protobuf.Collections;
using Parrot.Cli.Enhanced;
using Parrot.Protocol;

namespace Parrot.Cli;

internal static class AgentTaskProgressFormatter
{
    public static IReadOnlyList<string> Format(AgentTaskProgressSnapshot snapshot) =>
        [.. FormatRows(snapshot, null, null).Select(static row => row.Text)];

    /// <summary>
    /// Formats visible branches, retaining paths to active workers beneath hidden nodes. An agent line replaces
    /// the status icon, with the task description after it.
    /// </summary>
    internal static IReadOnlyList<AgentTaskRow> FormatRows(
        AgentTaskProgressSnapshot snapshot,
        IReadOnlyDictionary<string, TaskAgentLine>? agentLines,
        IReadOnlySet<string>? activeAgentSessionIds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var nodes = SelectVisibleNodes(snapshot.RootNodes, false, activeAgentSessionIds);
        if (nodes.Count == 0)
        {
            return [];
        }

        var rows = new List<AgentTaskRow> { new("Agent tasks:", string.Empty) };
        foreach (var node in nodes)
        {
            AppendNode(node, string.Empty, agentLines, rows);
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

    internal static IEnumerable<string> GetEmbeddedAgentSessionIds(
        AgentTaskProgressSnapshot snapshot,
        IReadOnlyDictionary<string, TaskAgentLine> agentLines,
        IReadOnlySet<string> activeAgentSessionIds) =>
        SelectVisibleNodes(snapshot.RootNodes, false, activeAgentSessionIds).SelectMany(Flatten)
            .Where(node => agentLines.ContainsKey(node.AgentSessionId))
            .Select(static node => node.AgentSessionId);

    internal static IEnumerable<string> GetVisibleChildGraphOwnerSessionIds(
        AgentTaskProgressSnapshot snapshot,
        IReadOnlySet<string> activeAgentSessionIds) =>
        SelectVisibleNodes(snapshot.RootNodes, false, activeAgentSessionIds).SelectMany(Flatten)
            .Where(static node => node.Children.Count > 0)
            .Select(static node => node.AgentSessionId);

    internal static string DisplayText(AgentTaskProgressNode node)
    {
        var text = string.IsNullOrEmpty(node.Description) ? node.Name : node.Description;
        return TerminalText.Sanitize(text).Replace('\n', ' ');
    }

    private static List<AgentTaskProgressNode> SelectVisibleNodes(
        IEnumerable<AgentTaskProgressNode> nodes,
        bool hiddenAncestor,
        IReadOnlySet<string>? activeAgentSessionIds)
    {
        var visible = new List<AgentTaskProgressNode>();
        foreach (var node in nodes)
        {
            var hidden = hiddenAncestor || node.Hidden;
            var children = SelectVisibleNodes(node.Children, hidden, activeAgentSessionIds);
            if (hidden && children.Count == 0 && activeAgentSessionIds?.Contains(node.AgentSessionId) != true)
            {
                continue;
            }

            var copy = node.Clone();
            copy.Children.Clear();
            copy.Children.Add(children);
            visible.Add(copy);
        }

        return visible;
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
            AppendNode(node, $"{ancestors}{(last ? "└──" : "├──")} ", agentLines, rows);
            Append(node.Children, descendants, agentLines, rows);
        }
    }

    private static void AppendNode(
        AgentTaskProgressNode node,
        string lead,
        IReadOnlyDictionary<string, TaskAgentLine>? agentLines,
        List<AgentTaskRow> rows)
    {
        if (agentLines is not null
            && agentLines.TryGetValue(node.AgentSessionId, out var agentLine))
        {
            rows.Add(AgentTaskRow.Create(lead, $"{agentLine.Text.TrimEnd()} {DisplayText(node)}") with
            {
                Status = node.Status,
                ModelAliasIcon = agentLine.ModelAliasIcon,
                GlyphStartIndex = lead.Length + agentLine.GlyphStartIndex,
            });
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
