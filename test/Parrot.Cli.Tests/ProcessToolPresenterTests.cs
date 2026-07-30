using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;

namespace Parrot.Cli.Tests;

internal sealed class ProcessToolPresenterTests
{
    private static readonly LiveBufferRenderContext LiveContext = new(32_768, new TerminalPalette(false));
    private static readonly ScrollbackRenderContext ScrollbackContext = new(32_768, new TerminalPalette(false));

    public static IEnumerable<Func<object?[]>> Presentations()
    {
        yield return () =>
        [
            new ExecCommandToolPresenter(),
            new ToolCallPresentation("main", "exec_command", "{\"command\":\"git status\",\"name\":\"git\"}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "git", string.Empty),
            "$ git status",
            "process git running",
        ];
        yield return () =>
        [
            new ExecCommandToolPresenter(),
            new ToolCallPresentation("main", "exec_command", "{\"command\":\"compile\",\"yield_after_ms\":0}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "shell-42", string.Empty),
            "$ compile",
            "process shell-42 running",
        ];
        yield return () =>
        [
            new ExecCommandToolPresenter(),
            new ToolCallPresentation("main", "exec_command", "{\"command\":\"compile\"}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "Process exited with code 7", string.Empty),
            "$ compile",
            "✗ main: $ compile",
        ];
        yield return () =>
        [
            new InterruptProcessToolPresenter(),
            new ToolCallPresentation("main", "interrupt_process", "{\"name\":\"build\"}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "Shell process 'build' interrupted.", string.Empty),
            "interrupt build",
            "interrupt build",
        ];
        yield return () =>
        [
            new InterruptProcessToolPresenter(),
            new ToolCallPresentation("main", "interrupt_process", "{\"name\":\"build\"}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "Process exited with code 2", string.Empty),
            "interrupt build",
            "✗ main: interrupt build",
        ];
    }

    [Test]
    public async Task Spilled_nonzero_process_output_is_reported_as_a_failure()
    {
        var presenter = new ExecCommandToolPresenter();
        var call = new ToolCallPresentation("main", "exec_command", "{\"command\":\"compile\"}");
        var outputPath = Path.GetFullPath(Path.Combine("state", "sessions", "session", "blob", "output"));
        var terminal = new ToolTerminalPresentation(
            ToolTerminalStatus.Succeeded,
            true,
            $"Process exited with code 7\nTool output exceeded 64 KiB and was saved to {outputPath}.",
            string.Empty);

        var rendered = presenter.PresentTerminal(call, terminal).Render(ScrollbackContext);

        _ = await Assert.That(terminal.ResolveProcessStatus()).IsEqualTo(ToolTerminalStatus.ReportedFailure);
        _ = await Assert.That(rendered[0]).IsEqualTo("✗ main: $ compile");
        _ = await Assert.That(string.Join('\n', rendered)).Contains($"saved to {outputPath}");
    }

    [Test]
    public async Task Active_exec_process_shows_elapsed_runtime_across_animation_frames()
    {
        var timeProvider = new ControlledTimeProvider();
        var presenter = new ExecCommandToolPresenter(timeProvider);
        var call = new ToolCallPresentation("main", "exec_command", "{\"command\":\"dotnet test\"}");
        var live = (ToolLiveValue)presenter.PresentLive(call, 0);

        _ = await Assert.That(live.Render(LiveContext).Lines[0].Text)
            .IsEqualTo("⠋ main: $ dotnet test (running 0s)");

        timeProvider.SetElapsed(TimeSpan.FromSeconds(12));
        live = live.Animate(1);
        _ = await Assert.That(live.Render(LiveContext).Lines[0].Text)
            .IsEqualTo("⠙ main: $ dotnet test (running 12s)");

        timeProvider.SetElapsed(TimeSpan.FromSeconds(125));
        live = live.Animate(2);
        _ = await Assert.That(live.Render(LiveContext).Lines[0].Text)
            .IsEqualTo("⠹ main: $ dotnet test (running 2m 05s)");

        timeProvider.SetElapsed(TimeSpan.FromSeconds(3723));
        live = live.Animate(3);
        _ = await Assert.That(live.Render(LiveContext).Lines[0].Text)
            .IsEqualTo("⠸ main: $ dotnet test (running 1h 02m 03s)");
    }

    [Test]
    public async Task Exec_process_output_is_not_colored()
    {
        var presenter = new ExecCommandToolPresenter();
        var call = new ToolCallPresentation("main", "exec_command", "{\"command\":\"echo output\"}");
        var terminal = new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "output", string.Empty);

        var rendered = presenter.PresentTerminal(call, terminal)
            .Render(new ScrollbackRenderContext(32_768, new TerminalPalette(true)));

        _ = await Assert.That(rendered[0]).Contains("\u001b[32m");
        _ = await Assert.That(rendered[1]).IsEqualTo("  output");
    }

    [Test]
    public async Task Wait_process_is_live_only_and_modeline_eligible()
    {
        var presenter = new WaitProcessToolPresenter();
        var call = new ToolCallPresentation("main", "wait_process", "{\"name\":\"build\"}");
        var terminal = new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "build", string.Empty);

        var live = (IToolPresentationValue)presenter.PresentLive(call, 0);

        _ = await Assert.That(live.Report.Metadata.LiveOnly).IsTrue();
        _ = await Assert.That(live.Report.Metadata.Modeline).IsTrue();
        _ = await Assert.That(presenter.PresentTerminal(call, terminal)).IsNull();
    }

    [Test]
    [MethodDataSource(nameof(Presentations))]
    public async Task Presenters_render_tool_specific_labels_details_and_reported_failures(
        IToolPresenter presenter,
        ToolCallPresentation call,
        ToolTerminalPresentation terminal,
        string liveExpected,
        string terminalExpected)
    {
        var live = presenter.PresentLive(call, 0).Render(LiveContext).Lines.Select(line => line.Text);
        var terminalLines = (presenter.PresentTerminal(call, terminal)
            ?? throw new InvalidOperationException("Presenter did not render terminal output")).Render(ScrollbackContext);

        _ = await Assert.That(string.Join('\n', live)).Contains(liveExpected);
        _ = await Assert.That(string.Join('\n', terminalLines)).Contains(terminalExpected);
    }

    private sealed class ControlledTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void SetElapsed(TimeSpan elapsed) => _timestamp = elapsed.Ticks;
    }
}
