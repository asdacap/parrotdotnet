using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;

namespace Parrot.Cli.Tests;

internal sealed class EnhancedAgentTodoToolPresenterTests
{
    private static readonly LiveBufferRenderContext LiveContext = new(512, new TerminalPalette(false));
    private static readonly ScrollbackRenderContext ScrollbackContext = new(512, new TerminalPalette(false));

    public static IEnumerable<Func<object?[]>> Presentations()
    {
        yield return () =>
        [
            new AgentSendToolPresenter(),
            new ToolCallPresentation(
                "main",
                "agent_send",
                "{\"session_id\":\"agent-session-opaque\",\"message\":\"inspect logs\"}",
                static reference => reference == "agent-session-opaque" ? "scout" : reference),
            "{\"session_id\":\"agent-session-opaque\",\"name\":\"scout\",\"status\":\"running\"}",
            "main: Send to scout",
            "✓ main: Send to scout|  inspect logs",
        ];
        yield return () =>
        [
            new AgentSpawnToolPresenter(),
            new ToolCallPresentation("main", "agent_spawn", "{\"prompt\":\"inspect logs\",\"name\":\"scout\",\"scope\":\"storage layer\"}"),
            "{\"name\":\"scout\",\"status\":\"running\"}",
            "main: Start agent scout",
            "♟ main: Start agent scout|name: scout|scope: storage layer|prompt: inspect logs",
        ];
        yield return () =>
        [
            new TodoReadToolPresenter(),
            new ToolCallPresentation("main", "todoread", "{}"),
            "[{\"content\":\"ship it\",\"status\":\"pending\"}]",
            "main: Todo list",
            "✓ main: Todo list|  ○ ship it",
        ];
        yield return () =>
        [
            new TodoWriteToolPresenter(),
            new ToolCallPresentation("main", "todowrite", "{\"todos\":[{\"content\":\"ship it\",\"status\":\"pending\",\"priority\":\"high\"}]}"),
            "[{\"content\":\"ship it\",\"status\":\"completed\",\"priority\":\"low\"}]",
            "main: TODO · 1 item|  ○ high · ship it",
            "✓ main: TODO · 1 item|  ✓ low · ship it",
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
    public async Task Agent_send_flushes_a_bounded_message()
    {
        var presenter = new AgentSendToolPresenter();
        var message = string.Join("\\n", Enumerable.Range(1, 12).Select(static line => $"line {line}"));
        var call = new ToolCallPresentation(
            "main",
            "agent_send",
            $"{{\"session_id\":\"scout\",\"message\":\"{message}\"}}");
        var terminal = new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "{}", string.Empty);

        var completed = presenter.PresentTerminal(call, terminal).Render(ScrollbackContext);

        _ = await Assert.That(completed[0]).IsEqualTo("✓ main: Send to scout");
        _ = await Assert.That(completed).Count().IsLessThanOrEqualTo(10);
        _ = await Assert.That(string.Join('|', completed)).Contains("  line 1");
        _ = await Assert.That(completed[^1]).Contains("lines truncated.");
        _ = await Assert.That(string.Join('|', completed)).DoesNotContain("line 12");
    }

    [Test]
    public async Task Agent_send_prefers_the_completed_recipient_name_and_falls_back_to_the_live_resolution()
    {
        var presenter = new AgentSendToolPresenter();
        var call = new ToolCallPresentation(
            "main",
            "agent_send",
            "{\"session_id\":\"agent-session-opaque\",\"message\":\"inspect logs\"}",
            static _ => "known-before-send");
        var completed = presenter.PresentTerminal(
            call,
            new ToolTerminalPresentation(
                ToolTerminalStatus.Succeeded,
                true,
                "{\"session_id\":\"agent-session-opaque\",\"name\":\"resolved-by-core\",\"status\":\"running\"}",
                string.Empty)).Render(ScrollbackContext);
        var legacy = presenter.PresentTerminal(
            call,
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "not json", string.Empty))
            .Render(ScrollbackContext);
        var nonObject = presenter.PresentTerminal(
            call,
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "[]", string.Empty))
            .Render(ScrollbackContext);
        var failed = presenter.PresentTerminal(
            call,
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "error: unavailable", string.Empty))
            .Render(ScrollbackContext);

        _ = await Assert.That(completed[0]).IsEqualTo("✓ main: Send to resolved-by-core");
        _ = await Assert.That(legacy[0]).IsEqualTo("✓ main: Send to known-before-send");
        _ = await Assert.That(nonObject[0]).IsEqualTo("✓ main: Send to known-before-send");
        _ = await Assert.That(failed[0]).IsEqualTo("✗ main: Send to known-before-send");
        _ = await Assert.That(string.Join('|', failed)).Contains("error: unavailable");
    }

    [Test]
    public async Task Todo_write_renders_priorities_and_empty_lists_like_the_reference_client()
    {
        var presenter = new TodoWriteToolPresenter();
        var call = new ToolCallPresentation(
            "main",
            "todowrite",
            "{\"todos\":[{\"content\":\"Plan work\",\"status\":\"pending\",\"priority\":\"high\"},{\"content\":\"Implement UI\",\"status\":\"in_progress\",\"priority\":\"medium\"},{\"content\":\"Run tests\",\"status\":\"completed\",\"priority\":\"low\"},{\"content\":\"Discard old approach\",\"status\":\"cancelled\",\"priority\":\"low\"}]}");
        var terminal = new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "[]", string.Empty);

        var live = presenter.PresentLive(call, 0).Render(LiveContext).Lines.Select(line => line.Text);
        var completed = (presenter.PresentTerminal(call, terminal)
            ?? throw new InvalidOperationException("Presenter did not render terminal output")).Render(ScrollbackContext);

        _ = await Assert.That(string.Join('|', live)).Contains("○ high · Plan work|  ◐ medium · Implement UI|  ✓ low · Run tests|  ■ low · Discard old approach");
        _ = await Assert.That(string.Join('|', completed)).Contains("✓ main: TODO · 0 items|  No todos");
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
