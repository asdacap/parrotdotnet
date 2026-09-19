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
        var rows = AgentTaskProgressFormatter.FormatRows(snapshot);

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
    [Arguments(0, "")]
    [Arguments(1, "  ")]
    [Arguments(2, "      ")]
    [Arguments(3, "          ")]
    public async Task FormatRows_hangs_each_row_at_the_start_of_its_description(int index, string indent)
    {
        var rows = AgentTaskProgressFormatter.FormatRows(Snapshot());

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

        var rows = AgentTaskProgressFormatter.FormatRows(snapshot);

        _ = await Assert.That(rows[1].Text).IsEqualTo("○ safe[2J    node");
        _ = await Assert.That(rows[1].HangingIndent).IsEqualTo("  ");
        _ = await Assert.That(TerminalText.Width(rows[1].HangingIndent)).IsEqualTo(2);
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
