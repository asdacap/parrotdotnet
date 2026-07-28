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
            "main: Send to scout|  inspect logs",
            "+ main: Send to scout|  inspect logs",
        ];
        yield return () =>
        [
            new AgentSpawnToolPresenter(),
            new ToolCallPresentation("main", "agent_spawn", "{\"prompt\":\"inspect logs\",\"name\":\"scout\"}"),
            "{\"name\":\"scout\",\"status\":\"running\"}",
            "main: Start agent|  scout|  inspect logs",
            "+ main: Start agent|  scout|  inspect logs",
        ];
        yield return () =>
        [
            new WaitAgentToolPresenter(),
            new ToolCallPresentation("main", "wait_agent", "{\"session_id\":\"scout\"}"),
            "{\"status\":\"succeeded\"}",
            "main: Wait for scout",
            "+ main: Wait for scout|  {\"status\":\"succeeded\"}",
        ];
        yield return () =>
        [
            new TodoReadToolPresenter(),
            new ToolCallPresentation("main", "todoread", "{}"),
            "[{\"content\":\"ship it\",\"status\":\"pending\"}]",
            "main: Todo list",
            "+ main: Todo list|  [pending] ship it",
        ];
        yield return () =>
        [
            new TodoWriteToolPresenter(),
            new ToolCallPresentation("main", "todowrite", "{\"todos\":[{\"content\":\"ship it\",\"status\":\"pending\"}]}"),
            "[{\"content\":\"ship it\",\"status\":\"completed\"}]",
            "main: Update todo list|  [pending] ship it",
            "+ main: Update todo list|  [completed] ship it",
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
    public async Task Wait_agent_marks_structured_failed_and_canceled_results()
    {
        var presenter = new WaitAgentToolPresenter();
        var call = new ToolCallPresentation("main", "wait_agent", "{\"session_id\":\"scout\"}");
        var failed = new ToolTerminalPresentation(
            ToolTerminalStatus.Succeeded,
            true,
            "{\"status\":\"failed\",\"error\":\"boom\"}",
            string.Empty);
        var canceled = failed with { Result = "{\"status\":\"canceled\"}" };

        var failedLines = (presenter.PresentTerminal(call, failed)
            ?? throw new InvalidOperationException("Presenter did not render terminal output")).Render(ScrollbackContext);
        var canceledLines = (presenter.PresentTerminal(call, canceled)
            ?? throw new InvalidOperationException("Presenter did not render terminal output")).Render(ScrollbackContext);

        _ = await Assert.That(failedLines[0]).IsEqualTo("! main: Wait for scout");
        _ = await Assert.That(canceledLines[0]).IsEqualTo("- main: Wait for scout");
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

        _ = await Assert.That(completed[0]).IsEqualTo($"! {expectedLabel}");
        _ = await Assert.That(string.Join('|', completed)).Contains("error: unavailable");
    }
}
