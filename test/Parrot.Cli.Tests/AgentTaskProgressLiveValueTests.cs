using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class AgentTaskProgressLiveValueTests
{
    private static readonly TerminalPalette Palette = new(false);

    [Test]
    [Arguments(AgentTaskProgressStatus.Pending, "○")]
    [Arguments(AgentTaskProgressStatus.Running, "◐")]
    [Arguments(AgentTaskProgressStatus.Succeeded, "✓")]
    [Arguments(AgentTaskProgressStatus.Failed, "✗")]
    [Arguments(AgentTaskProgressStatus.Canceled, "■")]
    public async Task Render_shows_each_status_icon(AgentTaskProgressStatus status, string icon)
    {
        var snapshot = new AgentTaskProgressSnapshot();
        snapshot.RootNodes.Add(new AgentTaskProgressNode { Name = "task", Status = status });

        var lines = Render(snapshot, 80);

        _ = await Assert.That(string.Join('|', lines)).IsEqualTo($"• Agent tasks:|  {icon} task");
    }

    [Test]
    public async Task Render_sanitizes_controls_and_flattens_newlines()
    {
        var snapshot = new AgentTaskProgressSnapshot();
        snapshot.RootNodes.Add(new AgentTaskProgressNode
        {
            Name = "safe\u001b[2J\tline\nnext",
            Status = AgentTaskProgressStatus.Running,
        });

        var lines = Render(snapshot, 80);

        _ = await Assert.That(lines[1]).IsEqualTo("  ◐ safe[2J    line next");
        _ = await Assert.That(string.Join('\n', lines)).DoesNotContain("\u001b");
    }

    [Test]
    public async Task Render_wraps_using_terminal_display_width_and_preserves_hanging_indent()
    {
        var snapshot = new AgentTaskProgressSnapshot();
        var root = new AgentTaskProgressNode { Name = "root", Status = AgentTaskProgressStatus.Running };
        root.Children.Add(new AgentTaskProgressNode { Name = "日本語 alpha beta", Status = AgentTaskProgressStatus.Pending });
        snapshot.RootNodes.Add(root);

        var lines = Render(snapshot, 14);

        _ = await Assert.That(string.Join('|', lines))
            .IsEqualTo("• Agent tasks:|  ◐ root|  └── ○ 日本語|        alpha|        beta");
    }

    [Test]
    public async Task Render_hangs_wrapped_rows_at_the_description_text_start()
    {
        var snapshot = new AgentTaskProgressSnapshot();
        var root = new AgentTaskProgressNode
        {
            Name = "root",
            Status = AgentTaskProgressStatus.Running,
        };
        root.Children.Add(new AgentTaskProgressNode
        {
            Description = "Inspect the poetry lock file and pin the transitive dependency",
            Status = AgentTaskProgressStatus.Pending,
        });
        snapshot.RootNodes.Add(root);
        var loneRoot = new AgentTaskProgressSnapshot();
        loneRoot.RootNodes.Add(new AgentTaskProgressNode
        {
            Description = "Execute an AgentTask payload and verify its observable result and report back",
            Status = AgentTaskProgressStatus.Running,
        });

        _ = await Assert.That(Live(snapshot, 30))
            .IsEqualTo(Scrollback(snapshot, 30));
        _ = await Assert.That(Live(snapshot, 30))
            .IsEqualTo(
                "• Agent tasks:|  ◐ root|  └── ○ Inspect the poetry|        lock file and pin the|" +
                "        transitive dependency");
        _ = await Assert.That(Live(loneRoot, 40))
            .IsEqualTo(Scrollback(loneRoot, 40));
        _ = await Assert.That(Live(loneRoot, 40))
            .IsEqualTo(
                "• Agent tasks:|  ◐ Execute an AgentTask payload and|    verify its observable result and|" +
                "    report back");
    }

    [Test]
    public async Task Render_draws_connectors_for_nested_siblings()
    {
        var snapshot = new AgentTaskProgressSnapshot();
        var root = new AgentTaskProgressNode { Name = "root", Status = AgentTaskProgressStatus.Running };
        var first = new AgentTaskProgressNode { Name = "first", Status = AgentTaskProgressStatus.Pending };
        first.Children.Add(new AgentTaskProgressNode { Name = "nested", Status = AgentTaskProgressStatus.Succeeded });
        root.Children.Add(first);
        root.Children.Add(new AgentTaskProgressNode { Name = "last", Status = AgentTaskProgressStatus.Failed });
        snapshot.RootNodes.Add(root);

        var lines = Render(snapshot, 80);

        _ = await Assert.That(string.Join('|', lines))
            .IsEqualTo("• Agent tasks:|  ◐ root|  ├── ○ first|  │   └── ✓ nested|  └── ✗ last");
    }

    [Test]
    public async Task Render_includes_every_row_when_tree_exceeds_ten_rows()
    {
        var snapshot = new AgentTaskProgressSnapshot();
        for (var index = 0; index < 12; index++)
        {
            snapshot.RootNodes.Add(new AgentTaskProgressNode
            {
                Name = $"task-{index}",
                Status = AgentTaskProgressStatus.Pending,
            });
        }

        var lines = Render(snapshot, 80);
        var rendered = string.Join('|', lines);

        _ = await Assert.That(lines).Count().IsEqualTo(13);
        _ = await Assert.That(rendered).Contains("task-0");
        _ = await Assert.That(rendered).Contains("task-11");
        _ = await Assert.That(rendered).DoesNotContain("… more tasks");
    }

    [Test]
    public async Task Render_uses_description_and_falls_back_to_name_for_legacy_nodes()
    {
        var snapshot = new AgentTaskProgressSnapshot();
        snapshot.RootNodes.Add(new AgentTaskProgressNode
        {
            Name = "internal-name",
            Description = "display\ntext\u001b[2J",
            Status = AgentTaskProgressStatus.Running,
        });
        snapshot.RootNodes.Add(new AgentTaskProgressNode
        {
            Name = "legacy-name",
            Status = AgentTaskProgressStatus.Pending,
        });

        var lines = Render(snapshot, 80);

        _ = await Assert.That(lines[1]).IsEqualTo("  ◐ display text[2J");
        _ = await Assert.That(lines[2]).IsEqualTo("  ○ legacy-name");
    }

    [Test]
    public async Task Presenter_redacts_path_and_tasks_from_live_but_retains_terminal_behavior()
    {
        IToolPresenter presenter = new SetAgentTasksToolPresenter(new GenericToolPresenter());
        var call = new ToolCallPresentation(
            "set_agent_tasks",
            "{\"path\":\"/private/task.json\",\"tasks\":[{\"secret\":\"embedded\"}]}");
        var live = presenter.PresentLive(call, 0);
        var terminal = presenter.PresentTerminal(
            call,
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "complete", string.Empty));

        var renderedLive = string.Join('\n', live.Render(new LiveBufferRenderContext(80, Palette)).Lines.Select(static line => line.Text));
        var renderedTerminal = terminal?.Render(new ScrollbackRenderContext(80, Palette))
            ?? throw new InvalidOperationException();

        _ = await Assert.That(renderedLive).IsEqualTo("⠋ setting agent tasks");
        _ = await Assert.That(renderedLive).DoesNotContain("/private/task.json");
        _ = await Assert.That(renderedLive).DoesNotContain("embedded");
        _ = await Assert.That(renderedTerminal[0]).IsEqualTo("✓ tool call set_agent_tasks");
        _ = await Assert.That(renderedTerminal[1]).IsEqualTo("  complete");
    }

    [Test]
    [Arguments(AgentTaskProgressStatus.Pending, "38;5;245")]
    [Arguments(AgentTaskProgressStatus.Running, "36")]
    [Arguments(AgentTaskProgressStatus.Succeeded, "32")]
    [Arguments(AgentTaskProgressStatus.Failed, "31")]
    [Arguments(AgentTaskProgressStatus.Canceled, "38;5;245")]
    public async Task Render_colors_status_rows_and_their_wrapped_continuations(
        AgentTaskProgressStatus status,
        string foreground)
    {
        var snapshot = new AgentTaskProgressSnapshot();
        snapshot.RootNodes.Add(new AgentTaskProgressNode
        {
            Description = "Inspect the poetry lock file and pin the transitive dependency",
            Status = status,
        });
        var palette = new TerminalPalette(true);
        var live = new AgentTaskProgressLiveValue(snapshot, null)
            .Render(new LiveBufferRenderContext(30, palette));
        var scrollback = new AgentTaskProgressScrollbackValue(snapshot)
            .Render(new ScrollbackRenderContext(30, palette));

        _ = await Assert.That(live.Lines.Count).IsGreaterThan(2);
        foreach (var line in live.Lines.Skip(1))
        {
            _ = await Assert.That(line.Style.Start).IsEqualTo($"\u001b[48;5;236m\u001b[{foreground}m");
        }

        foreach (var line in scrollback.Skip(1))
        {
            _ = await Assert.That(line.StartsWith($"\u001b[{foreground}m", StringComparison.Ordinal)).IsTrue();
            _ = await Assert.That(line.EndsWith(TerminalStyle.Reset, StringComparison.Ordinal)).IsTrue();
        }
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Render_preserves_embedded_agent_color_without_changing_task_status_color(bool color)
    {
        var snapshot = new AgentTaskProgressSnapshot();
        snapshot.RootNodes.Add(new AgentTaskProgressNode
        {
            Description = "Inspect dependencies",
            Status = AgentTaskProgressStatus.Running,
            AgentSessionId = "child",
        });
        var icon = new LiveModelAliasIcon("◆", Parrot.Llm.ModelAliasIconColor.Magenta);
        var agentLines = new Dictionary<string, TaskAgentLine>(StringComparer.Ordinal)
        {
            ["child"] = new("⠋ [child] ◆ working", icon),
        };
        var palette = new TerminalPalette(color);
        var live = new AgentTaskProgressLiveValue(snapshot, agentLines)
            .Render(new LiveBufferRenderContext(80, palette));

        _ = await Assert.That(live.Lines[1].Text).IsEqualTo("  ⠋ [child] ◆ working");
        _ = await Assert.That(live.Lines[1].Style).IsEqualTo(palette.GetLiveIconStyle(icon.Color));
        _ = await Assert.That(live.Lines[2].Style).IsEqualTo(palette.GetTaskStyle(AgentTaskProgressStatus.Running, true));
        if (!color)
        {
            _ = await Assert.That(live.Lines.All(static line => string.IsNullOrEmpty(line.Style.Start))).IsTrue();
        }
    }

    private static string[] Render(AgentTaskProgressSnapshot snapshot, int columns)
    {
        ILiveBufferItem value = new AgentTaskProgressLiveValue(snapshot, null);
        return [.. value.Render(new LiveBufferRenderContext(columns, Palette)).Lines.Select(static line => line.Text)];
    }

    private static string Live(AgentTaskProgressSnapshot snapshot, int columns) =>
        string.Join('|', Render(snapshot, columns));

    private static string Scrollback(AgentTaskProgressSnapshot snapshot, int columns) =>
        string.Join('|', new AgentTaskProgressScrollbackValue(snapshot)
            .Render(new ScrollbackRenderContext(columns, Palette)));
}
