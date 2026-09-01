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
    [Arguments(AgentTaskProgressStatus.Blocked, "⊘")]
    [Arguments(AgentTaskProgressStatus.Canceled, "■")]
    public async Task Render_shows_each_status_icon(AgentTaskProgressStatus status, string icon)
    {
        var snapshot = new AgentTaskProgressSnapshot();
        snapshot.RootNodes.Add(new AgentTaskProgressNode { Name = "task", Status = status });

        var lines = Render(snapshot, 80);

        _ = await Assert.That(string.Join('|', lines)).IsEqualTo($"Agent tasks:|{icon} task");
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

        _ = await Assert.That(lines[1]).IsEqualTo("◐ safe[2J    line next");
        _ = await Assert.That(string.Join('\n', lines)).DoesNotContain("\u001b");
    }

    [Test]
    public async Task Render_wraps_using_terminal_display_width_and_preserves_hanging_indent()
    {
        var snapshot = new AgentTaskProgressSnapshot();
        var root = new AgentTaskProgressNode { Name = "root", Status = AgentTaskProgressStatus.Running };
        root.Children.Add(new AgentTaskProgressNode { Name = "日本語 alpha beta", Status = AgentTaskProgressStatus.Pending });
        snapshot.RootNodes.Add(root);

        var lines = Render(snapshot, 12);

        _ = await Assert.That(string.Join('|', lines))
            .IsEqualTo("Agent tasks:|◐ root|└── ○ 日本語|     alpha b|    eta");
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
            .IsEqualTo("Agent tasks:|◐ root|├── ○ first|│   └── ✓ nested|└── ✗ last");
    }

    [Test]
    public async Task Render_limits_output_to_ten_rows_and_marks_truncation()
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

        _ = await Assert.That(lines).Count().IsEqualTo(10);
        _ = await Assert.That(lines[^1]).IsEqualTo("… more tasks");
        _ = await Assert.That(string.Join('|', lines)).DoesNotContain("task-8");
    }

    [Test]
    public async Task Presenter_redacts_path_and_artifact_from_live_but_retains_terminal_behavior()
    {
        var presenter = new RunAgentTasksToolPresenter();
        var call = new ToolCallPresentation(
            "main",
            "run_agent_tasks",
            "{\"path\":\"/private/task.json\",\"artifact\":{\"secret\":\"embedded\"}}");
        var live = presenter.PresentLive(call, 0);
        var terminal = presenter.PresentTerminal(
            call,
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "complete", string.Empty));

        var renderedLive = string.Join('\n', live.Render(new LiveBufferRenderContext(80, Palette)).Lines.Select(static line => line.Text));
        var renderedTerminal = terminal?.Render(new ScrollbackRenderContext(80, Palette))
            ?? throw new InvalidOperationException();

        _ = await Assert.That(renderedLive).IsEqualTo("⠋ main: running agent tasks");
        _ = await Assert.That(renderedLive).DoesNotContain("/private/task.json");
        _ = await Assert.That(renderedLive).DoesNotContain("embedded");
        _ = await Assert.That(renderedTerminal[0]).IsEqualTo("✓ main: tool call run_agent_tasks");
        _ = await Assert.That(renderedTerminal[1]).IsEqualTo("  complete");
    }

    private static string[] Render(AgentTaskProgressSnapshot snapshot, int columns) =>
        [.. new AgentTaskProgressLiveValue(snapshot)
            .Render(new LiveBufferRenderContext(columns, Palette))
            .Lines
            .Select(static line => line.Text)];
}
