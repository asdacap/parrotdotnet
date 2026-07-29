using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;

namespace Parrot.Cli.Tests;

internal sealed class ProcessAndPatchToolPresenterTests
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
        yield return () =>
        [
            new ApplyPatchToolPresenter(),
            new ToolCallPresentation("main", "apply_patch", "{\"patchText\":\"file.txt\\n<<<<<<< SEARCH\\na\\n=======\\nb\\n>>>>>>> REPLACE\"}"),
            new ToolTerminalPresentation(
                ToolTerminalStatus.Succeeded,
                true,
                "--- a/file.txt\n+++ b/file.txt\n@@ -1,1 +1,1 @@\n-a\n+b\n",
                string.Empty),
            "apply patch",
            "file.txt",
        ];
        yield return () =>
        [
            new ApplyPatchToolPresenter(),
            new ToolCallPresentation("main", "apply_patch", "{\"patchText\":\"file.txt\"}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "error: rejected", string.Empty),
            "apply patch",
            "✗ main: apply patch",
        ];
    }

    [Test]
    public async Task Spilled_nonzero_process_output_is_reported_as_a_failure()
    {
        var presenter = new ExecCommandToolPresenter();
        var call = new ToolCallPresentation("main", "exec_command", "{\"command\":\"compile\"}");
        var terminal = new ToolTerminalPresentation(
            ToolTerminalStatus.Succeeded,
            true,
            "Process exited with code 7\nTool output exceeded 64 KiB and was saved to /tmp/output. Use exec_command to read the file.",
            string.Empty);

        var rendered = presenter.PresentTerminal(call, terminal).Render(ScrollbackContext);

        _ = await Assert.That(terminal.ResolveProcessStatus()).IsEqualTo(ToolTerminalStatus.ReportedFailure);
        _ = await Assert.That(rendered[0]).IsEqualTo("✗ main: $ compile");
        _ = await Assert.That(string.Join('\n', rendered)).Contains("saved to /tmp/output");
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

    [Test]
    public async Task Apply_patch_renders_the_result_diff_not_aider_request_syntax()
    {
        var presenter = new ApplyPatchToolPresenter();
        var call = new ToolCallPresentation(
            "main",
            "apply_patch",
            "{\"patchText\":\"file.txt\\n<<<<<<< SEARCH\\na\\n=======\\nb\\n>>>>>>> REPLACE\"}");
        var terminal = new ToolTerminalPresentation(
            ToolTerminalStatus.Succeeded,
            true,
            "--- a/file.txt\n+++ b/file.txt\n@@ -1,1 +1,1 @@\n-a\n+b\n",
            string.Empty);

        var rendered = (presenter.PresentTerminal(call, terminal) ?? throw new InvalidOperationException())
            .Render(new ScrollbackRenderContext(80, new TerminalPalette(false)));
        var sideBySide = (presenter.PresentTerminal(call, terminal) ?? throw new InvalidOperationException())
            .Render(new ScrollbackRenderContext(80, new TerminalPalette(false), false));

        _ = await Assert.That(string.Join('\n', rendered)).Contains("1 -a");
        _ = await Assert.That(string.Join('\n', rendered)).Contains("1 +b");
        _ = await Assert.That(string.Join('\n', rendered)).DoesNotContain("│");
        _ = await Assert.That(string.Join('\n', sideBySide)).Contains("│ 1 +b");
        _ = await Assert.That(string.Join('\n', rendered)).DoesNotContain("--- a/file.txt");
        _ = await Assert.That(string.Join('\n', rendered)).DoesNotContain("+++ b/file.txt");
        _ = await Assert.That(string.Join('\n', rendered)).DoesNotContain("<<<<<<< SEARCH");
        _ = await Assert.That(string.Join('\n', rendered)).DoesNotContain(">>>>>>> REPLACE");
    }
}
