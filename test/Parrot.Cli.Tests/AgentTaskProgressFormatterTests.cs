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
        var rows = AgentTaskProgressFormatter.FormatRows(snapshot, null, null);

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
        var active = lines.Keys.ToHashSet(StringComparer.Ordinal);
        _ = await Assert.That(AgentTaskProgressFormatter.GetEmbeddedAgentSessionIds(nested, lines, active)).IsEmpty();
        _ = await Assert.That(string.Join('\n', AgentTaskProgressFormatter.FormatRows(nested, lines, active).Select(row => row.Text)))
            .IsEqualTo("Agent tasks:\n○ pending\n◐ starting");
    }

    [Test]
    [Arguments(0, "")]
    [Arguments(1, "  ")]
    [Arguments(2, "      ")]
    [Arguments(3, "          ")]
    public async Task FormatRows_hangs_each_row_at_the_start_of_its_description(int index, string indent)
    {
        var rows = AgentTaskProgressFormatter.FormatRows(Snapshot(), null, null);

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

        var rows = AgentTaskProgressFormatter.FormatRows(snapshot, null, null);

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

        var rows = AgentTaskProgressFormatter.FormatRows(snapshot, agentLines, agentLines.Keys.ToHashSet(StringComparer.Ordinal));

        _ = await Assert.That(string.Join('\n', rows.Select(static row => $"{row.HangingIndent.Length}:{row.Text}")))
            .IsEqualTo(
                "0:Agent tasks:\n" +
                "0:⠋ [root] agent root\n" +
                "2:  root description\n" +
                "4:├── ⠋ [first] agent first\n" +
                "6:│     first description\n" +
                "6:├── ◐ unseen description\n" +
                "4:└── ⠋ [done] agent done\n" +
                "6:      done description");
    }

    [Test]
    public async Task Hidden_branches_retain_only_paths_to_active_workers_without_mutating_flags()
    {
        var snapshot = Snapshot();
        var root = snapshot.RootNodes[0];
        root.Hidden = true;
        root.AgentSessionId = "root-agent";
        root.Children[0].AgentSessionId = "child-agent";
        root.Children[0].Children[0].AgentSessionId = "grandchild-agent";
        root.Children.Add(new AgentTaskProgressNode { Name = "idle sibling", AgentSessionId = "idle-agent" });
        var original = snapshot.Clone();
        var active = new HashSet<string>(StringComparer.Ordinal) { "grandchild-agent" };
        var lines = new Dictionary<string, TaskAgentLine>(StringComparer.Ordinal)
        {
            ["grandchild-agent"] = new("working grandchild", null),
        };

        var rows = AgentTaskProgressFormatter.FormatRows(snapshot, lines, active);

        _ = await Assert.That(string.Join('\n', rows.Select(static row => row.Text))).IsEqualTo(
            "Agent tasks:\n◐ root description\n└── ○ child description\n    └── working grandchild\n          日本語 説明\n✗ last root");
        _ = await Assert.That(snapshot.Equals(original)).IsTrue();
        _ = await Assert.That(AgentTaskProgressFormatter.GetEmbeddedAgentSessionIds(snapshot, lines, active))
            .IsEquivalentTo(["grandchild-agent"]);
        _ = await Assert.That(AgentTaskProgressFormatter.GetVisibleChildGraphOwnerSessionIds(snapshot, active))
            .IsEquivalentTo(["root-agent", "child-agent"]);
        _ = await Assert.That(AgentTaskProgressFormatter.GetVisibleChildGraphOwnerSessionIds(snapshot, new HashSet<string>(StringComparer.Ordinal)))
            .IsEmpty();
        _ = await Assert.That(string.Join('\n', AgentTaskProgressFormatter.Format(snapshot)))
            .IsEqualTo("Agent tasks:\n✗ last root");
    }

    [Test]
    public async Task Hidden_running_nodes_need_activity_and_filtered_siblings_have_correct_connectors()
    {
        var snapshot = Snapshot();
        snapshot.RootNodes[1].Hidden = true;
        var root = snapshot.RootNodes[0];
        root.Children[0].Hidden = true;
        root.Children.Add(new AgentTaskProgressNode { Name = "kept", Status = AgentTaskProgressStatus.Pending });
        root.Children.Add(new AgentTaskProgressNode { Name = "omitted", Hidden = true });

        _ = await Assert.That(string.Join('\n', AgentTaskProgressFormatter.Format(snapshot)))
            .IsEqualTo("Agent tasks:\n◐ root description\n└── ○ kept");
        root.Hidden = true;
        root.AgentSessionId = "worker";
        _ = await Assert.That(AgentTaskProgressFormatter.Format(snapshot)).IsEmpty();
        _ = await Assert.That(AgentTaskProgressFormatter.FormatRows(snapshot, null, new HashSet<string>(StringComparer.Ordinal) { "worker" })
            .Select(static row => row.Text)).IsEquivalentTo(["Agent tasks:", "◐ root description"]);
    }

    [Test]
    public async Task Nested_hidden_child_graphs_remain_empty_until_the_child_worker_is_active()
    {
        var owner = new AgentTaskProgressSnapshot
        {
            RootNodes = { new AgentTaskProgressNode { Name = "parent", Status = AgentTaskProgressStatus.Pending, AgentSessionId = "parent-agent" } },
        };
        var children = new AgentTaskProgressSnapshot
        {
            RootNodes = { new AgentTaskProgressNode { Name = "finished child", Hidden = true, Status = AgentTaskProgressStatus.Succeeded, AgentSessionId = "child-agent" } },
        };
        var nested = AgentTaskProgressFormatter.Nest(owner, new Dictionary<string, AgentTaskProgressSnapshot>(StringComparer.Ordinal) { ["parent-agent"] = children });

        _ = await Assert.That(string.Join('\n', AgentTaskProgressFormatter.Format(nested))).IsEqualTo("Agent tasks:\n○ parent");
        _ = await Assert.That(string.Join('\n', AgentTaskProgressFormatter.FormatRows(nested, null, new HashSet<string>(StringComparer.Ordinal) { "child-agent" })
            .Select(static row => row.Text))).IsEqualTo("Agent tasks:\n○ parent\n└── ✓ finished child");
        _ = await Assert.That(children.RootNodes[0].Hidden).IsTrue();
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
