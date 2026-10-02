using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;

namespace Parrot.Cli.Tests;

internal sealed class EnhancedAgentToolPresenterTests
{
    private static readonly LiveBufferRenderContext LiveContext = new(512, new TerminalPalette(false));
    private static readonly ScrollbackRenderContext ScrollbackContext = new(512, new TerminalPalette(false));

    public static IEnumerable<Func<object?[]>> Presentations()
    {
        yield return () =>
        [
            new AgentStatusToolPresenter(),
            new ToolCallPresentation(
                "agent_status",
                "{\"name\":\"agent-session-opaque\"}",
                static reference => reference == "agent-session-opaque" ? "scout" : reference),
            "Agent status\nName: scout\nLifecycle: running\nRequest session duration: 12.0s\nProvider request duration: none\n"
                + "Current activity: none\nActive direct subagents: none\nActive processes: none\nRecent entries:\n"
                + "- assistant message (1.0s ago): one\n- assistant message (0.5s ago): two\n- assistant message (10ms ago): three",
            "Agent status · scout",
            "✓ Agent status · scout|  Agent status|  Name: scout|  Lifecycle: running|  Request session duration: 12.0s|"
                + "  Provider request duration: none|  Current activity: none|  Active direct subagents: none|"
                + "  Active processes: none|  Recent entries:|  - assistant message (1.0s ago): one|"
                + "  - assistant message (0.5s ago): two|  - assistant message (10ms ago): three",
        ];
        yield return () =>
        [
            new AnswerToolPresenter(),
            new ToolCallPresentation(
                "answer",
                "{\"agent_name\":\"agent-session-opaque\",\"answers\":[\"use postgres\"]}",
                static reference => reference == "agent-session-opaque" ? "scout" : reference),
            "Answered the pending question from child agent scout.",
            "Answer · scout · 1 item|  use postgres",
            "✓ Answer · scout · 1 item|  use postgres",
        ];
        yield return () =>
        [
            new AnswerToolPresenter(),
            new ToolCallPresentation("answer", "{\"agent_name\":\"scout\",\"answers\":[\"yes\",\"keep it\\nbut rename\"]}"),
            "Answered the pending question from child agent scout.",
            "Answer · scout · 2 items|  1. yes|  2. keep it|     but rename",
            "✓ Answer · scout · 2 items|  1. yes|  2. keep it|     but rename",
        ];
        yield return () =>
        [
            new AgentSendToolPresenter(),
            new ToolCallPresentation(
                "agent_send",
                "{\"name\":\"agent-session-opaque\",\"message\":\"inspect logs\"}",
                static reference => reference == "agent-session-opaque" ? "scout" : reference),
            "{\"name\":\"scout\",\"status\":\"running\"}",
            "Send to scout",
            "✓ Send to scout|  inspect logs",
        ];
        yield return () =>
        [
            new AgentSpawnToolPresenter(),
            new ToolCallPresentation("agent_spawn", "{\"prompt\":\"inspect logs\",\"name\":\"scout\",\"scope\":\"storage layer\"}"),
            "{\"name\":\"scout\",\"status\":\"running\"}",
            "Start agent scout",
            "♟ Start agent scout|  name: scout|  scope: storage layer|  fork: empty|  prompt: inspect logs",
        ];
        yield return () =>
        [
            new SetCheckpointToolPresenter(),
            new ToolCallPresentation("set_checkpoint", "{\"title\":\"before refactor\"}"),
            "checkpoint set",
            "Set checkpoint before refactor",
            "✓ Set checkpoint before refactor",
        ];
        yield return () =>
        [
            new SetExitReminderToolPresenter(),
            new ToolCallPresentation("set_exit_reminder", "{\"title\":\"tests\",\"description\":\"run the suite\"}"),
            "exit reminder set",
            "Set exit reminder · tests · run the suite",
            "✓ Set exit reminder · tests · run the suite",
        ];
        yield return () =>
        [
            new ClearExitReminderToolPresenter(),
            new ToolCallPresentation("clear_exit_reminder", "{\"title\":\"tests\"}"),
            "exit reminder cleared",
            "Clear exit reminder · tests",
            "✓ Clear exit reminder · tests",
        ];
    }

    public static IEnumerable<Func<object?[]>> FailingPresentations()
    {
        foreach (var presentation in Presentations())
        {
            var values = presentation();
            yield return () => [values[0], values[1], (values[3]?.ToString() ?? throw new InvalidOperationException("Missing label")).Split('|')[0]];
        }
    }

    [Test]
    [MethodDataSource(nameof(Presentations))]
    public async Task Presenters_render_useful_live_and_terminal_details(
        IToolPresenter presenter,
        ToolCallPresentation call,
        string result,
        string expectedLive,
        string expectedTerminal)
    {
        var terminal = new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, result, string.Empty);

        var live = presenter.PresentLive(call, 0).Render(LiveContext).Lines.Select(line => line.Text);
        var completed = (presenter.PresentTerminal(call, terminal)
            ?? throw new InvalidOperationException("Presenter did not render terminal output")).Render(ScrollbackContext);

        _ = await Assert.That(string.Join('|', live)).Contains(expectedLive);
        _ = await Assert.That(string.Join('|', completed)).Contains(expectedTerminal);
    }

    [Test]
    public async Task Agent_spawn_renders_empty_full_and_checkpoint_forks()
    {
        IToolPresenter presenter = new AgentSpawnToolPresenter();
        var terminal = new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "{}", string.Empty);

        var omitted = (presenter.PresentTerminal(
            new ToolCallPresentation("agent_spawn", "{\"prompt\":\"inspect\",\"agent\":\"worker\"}"),
            terminal)
            ?? throw new InvalidOperationException("Terminal presentation missing.")).Render(ScrollbackContext);
        var empty = (presenter.PresentTerminal(
            new ToolCallPresentation("agent_spawn", "{\"prompt\":\"inspect\",\"agent\":\"worker\",\"fork\":\"\"}"),
            terminal)
            ?? throw new InvalidOperationException("Terminal presentation missing.")).Render(ScrollbackContext);
        var full = (presenter.PresentTerminal(
            new ToolCallPresentation("agent_spawn", "{\"prompt\":\"inspect\",\"agent\":\"worker\",\"fork\":\"full\"}"),
            terminal)
            ?? throw new InvalidOperationException("Terminal presentation missing.")).Render(ScrollbackContext);
        var checkpoint = (presenter.PresentTerminal(
            new ToolCallPresentation("agent_spawn", "{\"prompt\":\"inspect\",\"agent\":\"worker\",\"fork\":\"before refactor\"}"),
            terminal)
            ?? throw new InvalidOperationException("Terminal presentation missing.")).Render(ScrollbackContext);

        _ = await Assert.That(string.Join('|', omitted)).Contains("fork: empty");
        _ = await Assert.That(string.Join('|', empty)).Contains("fork: empty");
        _ = await Assert.That(string.Join('|', full)).Contains("fork: full");
        _ = await Assert.That(string.Join('|', checkpoint)).Contains("fork: before refactor");
    }

    [Test]
    public async Task Agent_send_flushes_a_bounded_message()
    {
        IToolPresenter presenter = new AgentSendToolPresenter();
        var message = string.Join("\\n", Enumerable.Range(1, 12).Select(static line => $"line {line}"));
        var call = new ToolCallPresentation(
            "agent_send",
            $"{{\"name\":\"scout\",\"message\":\"{message}\"}}");
        var terminal = new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "{}", string.Empty);

        var completed = (presenter.PresentTerminal(call, terminal) ?? throw new InvalidOperationException("Terminal presentation missing.")).Render(ScrollbackContext);

        _ = await Assert.That(completed[0]).IsEqualTo("✓ Send to scout");
        _ = await Assert.That(completed).Count().IsLessThanOrEqualTo(10);
        _ = await Assert.That(string.Join('|', completed)).Contains("  line 1");
        _ = await Assert.That(completed[^1]).Contains("lines truncated.");
        _ = await Assert.That(string.Join('|', completed)).DoesNotContain("line 12");
    }

    [Test]
    public async Task Agent_send_prefers_the_completed_recipient_name_and_falls_back_to_the_live_resolution()
    {
        IToolPresenter presenter = new AgentSendToolPresenter();
        var call = new ToolCallPresentation(
            "agent_send",
            "{\"name\":\"agent-session-opaque\",\"message\":\"inspect logs\"}",
            static _ => "known-before-send");
        var completed = (presenter.PresentTerminal(
            call,
            new ToolTerminalPresentation(
                ToolTerminalStatus.Succeeded,
                true,
                "{\"name\":\"resolved-by-core\",\"status\":\"running\"}",
                string.Empty))
            ?? throw new InvalidOperationException("Terminal presentation missing.")).Render(ScrollbackContext);
        var legacy = (presenter.PresentTerminal(
            call,
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "not json", string.Empty))
            ?? throw new InvalidOperationException("Terminal presentation missing."))
            .Render(ScrollbackContext);
        var nonObject = (presenter.PresentTerminal(
            call,
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "[]", string.Empty))
            ?? throw new InvalidOperationException("Terminal presentation missing."))
            .Render(ScrollbackContext);
        var failed = (presenter.PresentTerminal(
            call,
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "error: unavailable", string.Empty))
            ?? throw new InvalidOperationException("Terminal presentation missing."))
            .Render(ScrollbackContext);

        _ = await Assert.That(completed[0]).IsEqualTo("✓ Send to resolved-by-core");
        _ = await Assert.That(legacy[0]).IsEqualTo("✓ Send to known-before-send");
        _ = await Assert.That(nonObject[0]).IsEqualTo("✓ Send to known-before-send");
        _ = await Assert.That(failed[0]).IsEqualTo("✗ Send to known-before-send");
        _ = await Assert.That(string.Join('|', failed)).Contains("error: unavailable");
    }

    [Test]
    [MethodDataSource(nameof(FailingPresentations))]
    public async Task Presenters_mark_reported_error_results_as_failures(
        IToolPresenter presenter,
        ToolCallPresentation call,
        string expectedLabel)
    {
        var terminal = new ToolTerminalPresentation(
            ToolTerminalStatus.Succeeded,
            true,
            "error: unavailable",
            string.Empty);

        var completed = (presenter.PresentTerminal(call, terminal)
            ?? throw new InvalidOperationException("Presenter did not render terminal output")).Render(ScrollbackContext);

        _ = await Assert.That(completed[0]).IsEqualTo($"✗ {expectedLabel}");
        _ = await Assert.That(string.Join('|', completed)).Contains("error: unavailable");
    }
}
