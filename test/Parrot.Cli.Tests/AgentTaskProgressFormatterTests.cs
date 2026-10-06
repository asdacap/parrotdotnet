using Parrot.Cli.Enhanced;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class AgentTaskProgressFormatterTests
{
    [Test]
    public async Task Format_stays_byte_identical_while_FormatRows_carries_the_same_text()
    {
        var snapshot = Snapshot();

        var formatted = AgentTaskProgressFormatter.Format(snapshot);
        var rows = AgentTaskProgressFormatter.FormatRows(snapshot, null);

        _ = await Assert.That(string.Join('\n', formatted)).IsEqualTo(
            "Agent tasks:\n" +
            "◐ root description\n" +
            "└── ○ child description\n" +
            "    └── ✓ 日本語 説明\n" +
            "✗ last root");
        _ = await Assert.That(string.Join('\n', rows.Select(static row => row.Text)))
            .IsEqualTo(string.Join('\n', formatted));
    }

    [Test]
    public async Task Nest_places_each_known_child_agent_graph_under_its_task_without_changing_the_source()
    {
        var owner = new AgentTaskProgressSnapshot
        {
            Revision = 3,
            RootNodes =
            {
                new AgentTaskProgressNode { Name = "composite", Status = AgentTaskProgressStatus.Running, AgentSessionId = "composite-agent" },
                new AgentTaskProgressNode { Name = "leaf", Status = AgentTaskProgressStatus.Pending, AgentSessionId = "leaf-agent" },
            },
        };
        var trees = new Dictionary<string, AgentTaskProgressSnapshot>(StringComparer.Ordinal)
        {
            ["composite-agent"] = new()
            {
                RootNodes =
                {
                    new AgentTaskProgressNode { Name = "inner", Status = AgentTaskProgressStatus.Running, AgentSessionId = "inner-agent" },
                    new AgentTaskProgressNode { Name = "after", Status = AgentTaskProgressStatus.Pending },
                },
            },
            ["inner-agent"] = new() { RootNodes = { new AgentTaskProgressNode { Name = "deepest", Status = AgentTaskProgressStatus.Succeeded } } },
        };

        var nested = AgentTaskProgressFormatter.Nest(owner, trees);

        _ = await Assert.That(string.Join('\n', AgentTaskProgressFormatter.Format(nested))).IsEqualTo(
            "Agent tasks:\n" +
            "◐ composite\n" +
            "├── ◐ inner\n" +
            "│   └── ✓ deepest\n" +
            "└── ○ after\n" +
            "○ leaf");
        _ = await Assert.That(nested.Revision).IsEqualTo(3UL);
        _ = await Assert.That(owner.RootNodes[0].Children).IsEmpty();
    }

    [Test]
    public async Task Pending_and_not_yet_materialized_running_tasks_have_no_agent_links_or_nested_graphs()
    {
        var snapshot = new AgentTaskProgressSnapshot
        {
            RootNodes =
            {
                new AgentTaskProgressNode { Name = "pending", Status = AgentTaskProgressStatus.Pending },
                new AgentTaskProgressNode { Name = "starting", Status = AgentTaskProgressStatus.Running },
            },
        };
        var trees = new Dictionary<string, AgentTaskProgressSnapshot>(StringComparer.Ordinal)
        {
            ["other-agent"] = new() { RootNodes = { new AgentTaskProgressNode { Name = "unrelated" } } },
        };
        var lines = new Dictionary<string, TaskAgentLine>(StringComparer.Ordinal) { ["other-agent"] = new("unrelated agent line", null) };
        var nested = AgentTaskProgressFormatter.Nest(snapshot, trees);
        _ = await Assert.That(AgentTaskProgressFormatter.RunningAgentSessionIds(nested)).IsEmpty();
        _ = await Assert.That(string.Join('\n', AgentTaskProgressFormatter.FormatRows(nested, lines).Select(row => row.Text)))
            .IsEqualTo("Agent tasks:\n○ pending\n◐ starting");
    }

    [Test]
    [Arguments(0, "")]
    [Arguments(1, "  ")]
    [Arguments(2, "      ")]
    [Arguments(3, "          ")]
    public async Task FormatRows_hangs_each_row_at_the_start_of_its_description(int index, string indent)
    {
        var rows = AgentTaskProgressFormatter.FormatRows(Snapshot(), null);

        _ = await Assert.That(rows[index].HangingIndent).IsEqualTo(indent);
    }

    [Test]
    public async Task Format_sanitizes_and_flattens_the_display_text()
    {
        var snapshot = new AgentTaskProgressSnapshot();
        snapshot.RootNodes.Add(new AgentTaskProgressNode
        {
            Name = "safe\u001b[2J\tnode",
            Status = AgentTaskProgressStatus.Pending,
        });

        var rows = AgentTaskProgressFormatter.FormatRows(snapshot, null);

        _ = await Assert.That(rows[1].Text).IsEqualTo("○ safe[2J    node");
        _ = await Assert.That(rows[1].HangingIndent).IsEqualTo("  ");
        _ = await Assert.That(TerminalText.Width(rows[1].HangingIndent)).IsEqualTo(2);
    }

    [Test]
    public async Task FormatRows_puts_running_agent_lines_above_their_descriptions()
    {
        var root = new AgentTaskProgressNode
        {
            Name = "root",
            Description = "root description",
            Status = AgentTaskProgressStatus.Running,
            AgentSessionId = "root-agent",
        };
        root.Children.Add(new AgentTaskProgressNode
        {
            Name = "first",
            Description = "first description",
            Status = AgentTaskProgressStatus.Running,
            AgentSessionId = "first-agent",
        });
        root.Children.Add(new AgentTaskProgressNode
        {
            Name = "unseen",
            Description = "unseen description",
            Status = AgentTaskProgressStatus.Running,
            AgentSessionId = "unseen-agent",
        });
        root.Children.Add(new AgentTaskProgressNode
        {
            Name = "done",
            Description = "done description",
            Status = AgentTaskProgressStatus.Succeeded,
            AgentSessionId = "done-agent",
        });
        var snapshot = new AgentTaskProgressSnapshot();
        snapshot.RootNodes.Add(root);
        var agentLines = new Dictionary<string, TaskAgentLine>(StringComparer.Ordinal)
        {
            ["root-agent"] = new("⠋ [root] agent root", null),
            ["first-agent"] = new("⠋ [first] agent first", null),
            ["done-agent"] = new("⠋ [done] agent done", null),
        };

        var rows = AgentTaskProgressFormatter.FormatRows(snapshot, agentLines);

        _ = await Assert.That(string.Join('\n', rows.Select(static row => $"{row.HangingIndent.Length}:{row.Text}")))
            .IsEqualTo(
                "0:Agent tasks:\n" +
                "0:⠋ [root] agent root\n" +
                "2:  root description\n" +
                "4:├── ⠋ [first] agent first\n" +
                "6:│     first description\n" +
                "6:├── ◐ unseen description\n" +
                "6:└── ✓ done description");
    }

    private static AgentTaskProgressSnapshot Snapshot()
    {
        var grandchild = new AgentTaskProgressNode
        {
            Name = "grandchild",
            Description = "日本語 説明",
            Status = AgentTaskProgressStatus.Succeeded,
        };
        var child = new AgentTaskProgressNode
        {
            Name = "child",
            Description = "child description",
            Status = AgentTaskProgressStatus.Pending,
        };
        child.Children.Add(grandchild);
        var root = new AgentTaskProgressNode
        {
            Name = "root",
            Description = "root description",
            Status = AgentTaskProgressStatus.Running,
        };
        root.Children.Add(child);
        var snapshot = new AgentTaskProgressSnapshot();
        snapshot.RootNodes.Add(root);
        snapshot.RootNodes.Add(
            new AgentTaskProgressNode { Name = "last root", Status = AgentTaskProgressStatus.Failed });
        return snapshot;
    }
}
