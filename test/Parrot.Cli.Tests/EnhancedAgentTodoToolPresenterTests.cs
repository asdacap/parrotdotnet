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
            new ToolCallPresentation("main", "agent_send", "{\"session_id\":\"scout\",\"message\":\"inspect logs\"}"),
            "{\"status\":\"running\"}",
            "main: Send to scout",
            "✓ main: Send to scout",
        ];
        yield return () =>
        [
            new AgentSpawnToolPresenter(),
            new ToolCallPresentation("main", "agent_spawn", "{\"prompt\":\"inspect logs\",\"name\":\"scout\"}"),
            "{\"name\":\"scout\",\"status\":\"running\"}",
            "main: Start agent scout",
            "♟ main: Start agent scout|name: scout|prompt: inspect logs",
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
