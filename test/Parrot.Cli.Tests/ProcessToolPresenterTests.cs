using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Core.Tests;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class ProcessToolPresenterTests
{
    private static readonly LiveBufferRenderContext LiveContext = new(32_768, new TerminalPalette(false));
    private static readonly ScrollbackRenderContext ScrollbackContext = new(32_768, new TerminalPalette(false));

    public static IEnumerable<Func<object?[]>> Presentations()
    {
        yield return () =>
        [
            new ExecCommandToolPresenter(TimeProvider.System, []),
            new ToolCallPresentation("exec_command", "{\"command\":\"compile\"}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "Process exited with code 7 after 1.23s", string.Empty),
            "$ compile",
            "✗ $ compile",
        ];
        yield return () =>
        [
            new InterruptProcessToolPresenter(),
            new ToolCallPresentation("interrupt_process", "{\"name\":\"build\",\"signal\":15}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "Signal 15 sent to shell process 'build'.", string.Empty),
            "signal 15 build",
            "signal 15 build",
        ];
        yield return () =>
        [
            new InterruptProcessToolPresenter(),
            new ToolCallPresentation("interrupt_process", "{\"name\":\"build\"}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "Process exited with code 2 after 0.42s", string.Empty),
            "signal 2 build",
            "✗ signal 2 build",
        ];
        yield return () =>
        [
            new InterruptProcessToolPresenter(),
            new ToolCallPresentation("interrupt_process", "{\"name\":\"build\",\"signal\":null}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "Signal 2 sent to shell process 'build'.", string.Empty),
            "signal 2 build",
            "signal 2 build",
        ];
    }

    [Test]
    public async Task Yielded_exec_process_defers_terminal_presentation()
    {
        IToolPresenter presenter = new ExecCommandToolPresenter(TimeProvider.System, []);
        var call = new ToolCallPresentation("exec_command", "{\"command\":\"compile\"}");
        var terminal = new ToolTerminalPresentation(
            ToolTerminalStatus.Succeeded,
            true,
            "shell-42",
            string.Empty,
            new YieldedShellProcess
            {
                ProcessId = "process-2",
                Name = "shell-42",
                InventoryInstanceId = "inventory",
                VisibleRevision = 1,
                StdoutPath = "/state/stdout",
                StderrPath = "/state/stderr",
            },
            []);

        _ = await Assert.That(presenter.PresentTerminal(call, terminal)).IsNull();
    }

    [Test]
    [Arguments("{\"command\":\"status\",\"name\":\"git\"}", "git")]
    [Arguments("{\"command\":\"status\"}", "shell-42")]
    public async Task Exec_output_does_not_imply_a_yielded_process(string arguments, string result)
    {
        IToolPresenter presenter = new ExecCommandToolPresenter(TimeProvider.System, []);
        var call = new ToolCallPresentation("exec_command", arguments);
        var terminal = new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, result, string.Empty);

        var rendered = (presenter.PresentTerminal(call, terminal) ?? throw new InvalidOperationException()).Render(ScrollbackContext);

        _ = await Assert.That(rendered[0]).IsEqualTo("✓ $ status");
        _ = await Assert.That(string.Join('\n', rendered)).Contains(result);
        _ = await Assert.That(string.Join('\n', rendered)).DoesNotContain("process " + result + " running");
    }

    [Test]
    public async Task Spilled_nonzero_process_output_is_reported_as_a_failure()
    {
        IToolPresenter presenter = new ExecCommandToolPresenter(TimeProvider.System, []);
        var call = new ToolCallPresentation("exec_command", "{\"command\":\"compile\"}");
        var outputPath = Path.GetFullPath(Path.Combine("state", "sessions", "session", "blob", "output"));
        var terminal = new ToolTerminalPresentation(
            ToolTerminalStatus.Succeeded,
            true,
            $"Process exited with code 7 after 3.14s\nTool output exceeded 64 KiB and was saved to {outputPath}.",
            string.Empty);

        var rendered = (presenter.PresentTerminal(call, terminal) ?? throw new InvalidOperationException()).Render(ScrollbackContext);

        _ = await Assert.That(terminal.ResolveProcessStatus()).IsEqualTo(ToolTerminalStatus.ReportedFailure);
        _ = await Assert.That(rendered[0]).IsEqualTo("✗ $ compile");
        _ = await Assert.That(string.Join('\n', rendered)).Contains($"saved to {outputPath}");
    }

    [Test]
    public async Task Multiline_exec_command_shows_five_lines_and_reports_omitted_lines()
    {
        IToolPresenter presenter = new ExecCommandToolPresenter(TimeProvider.System, []);
        var command = "python3 - <<'PY'\nfirst\nsecond\nthird\nfourth\nfifth\nPY";
        var call = new ToolCallPresentation(
            "exec_command",
            "{\"command\":\"" + System.Text.Json.JsonEncodedText.Encode(command) + "\"}");
        var terminal = new ToolTerminalPresentation(
            ToolTerminalStatus.Succeeded,
            true,
            "Process exited with code 0 after 0.02s",
            string.Empty);

        var rendered = (presenter.PresentTerminal(call, terminal) ?? throw new InvalidOperationException())
            .Render(ScrollbackContext);

        _ = await Assert.That(string.Join('\n', rendered.Take(6))).IsEqualTo(
            "✓ $ python3 - <<'PY'\n  first\n  second\n  third\n  fourth\n  .. 2 lines truncated.");
    }

    [Test]
    [Arguments("python3 - <<'PY'\n", 200, "", null, "⠋ $ python3 - <<'PY'", "✓ $ python3 - <<'PY'", null)]
    [Arguments("python3 - <<'PY'\n", 200, ",\"name\":\"migrate\",\"description\":\"Rewrite config keys\"", "○ $ migrate · Rewrite config keys\n  python3 - <<'PY'", "⠋ $ migrate · Rewrite config keys (running 0s)", "✓ $ migrate · Rewrite config keys", "✓ $ migrate · Rewrite config keys\n  python3 - <<'PY'")]
    [Arguments("echo ", 10, ",\"name\":\"migrate\",\"description\":\"Rewrite config keys\"", null, "⠋ $ echo xxxxxxxxxx (running 0s)", "✓ $ echo xxxxxxxxxx", null)]
    public async Task Long_exec_command_is_committed_on_start_and_shows_only_its_description_afterwards(
        string prefix,
        int padding,
        string extraArguments,
        string? started,
        string live,
        string completed,
        string? completedWithoutStart)
    {
        IToolPresenter presenter = new ExecCommandToolPresenter(new ControlledTimeProvider(), []);
        var command = prefix + new string('x', padding);
        var call = new ToolCallPresentation(
            "exec_command",
            "{\"command\":\"" + System.Text.Json.JsonEncodedText.Encode(command) + "\"" + extraArguments + "}");
        var terminal = new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "Process exited with code 0 after 0.02s", string.Empty);

        _ = await Assert.That(presenter.PresentStarted(call) is { } startedItem
            ? string.Join('\n', startedItem.Render(ScrollbackContext).Take(2))
            : null).IsEqualTo(started);
        _ = await Assert.That(presenter.PresentLive(call, 0).Render(LiveContext).Lines[0].Text).IsEqualTo(live);
        _ = await Assert.That((presenter.PresentTerminal(call, terminal) ?? throw new InvalidOperationException())
            .Render(ScrollbackContext)[0]).IsEqualTo(completed);
        _ = await Assert.That(started is null
            ? null
            : string.Join('\n', (presenter.PresentTerminal(call, terminal with { StartOmitted = true }) ?? throw new InvalidOperationException())
                .Render(ScrollbackContext).Take(2))).IsEqualTo(completedWithoutStart);
    }

    [Test]
    public async Task Active_exec_process_shows_elapsed_runtime_across_animation_frames()
    {
        var timeProvider = new ControlledTimeProvider();
        IToolPresenter presenter = new ExecCommandToolPresenter(timeProvider, []);
        var call = new ToolCallPresentation("exec_command", "{\"command\":\"dotnet test\"}");
        var live = presenter.PresentLive(call, 0);

        _ = await Assert.That(live.Render(LiveContext).Lines[0].Text)
            .IsEqualTo("⠋ $ dotnet test (running 0s)");

        timeProvider.Advance(TimeSpan.FromSeconds(12));
        live = live.Animate(1);
        _ = await Assert.That(live.Render(LiveContext).Lines[0].Text)
            .IsEqualTo("⠙ $ dotnet test (running 12s)");

        timeProvider.Advance(TimeSpan.FromSeconds(125 - 12));
        live = live.Animate(2);
        _ = await Assert.That(live.Render(LiveContext).Lines[0].Text)
            .IsEqualTo("⠹ $ dotnet test (running 2m 05s)");

        timeProvider.Advance(TimeSpan.FromSeconds(3723 - 125));
        live = live.Animate(3);
        _ = await Assert.That(live.Render(LiveContext).Lines[0].Text)
            .IsEqualTo("⠸ $ dotnet test (running 1h 02m 03s)");
    }

    [Test]
    public async Task Exec_process_output_is_not_colored()
    {
        IToolPresenter presenter = new ExecCommandToolPresenter(TimeProvider.System, []);
        var call = new ToolCallPresentation("exec_command", "{\"command\":\"echo output\"}");
        var terminal = new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "output", string.Empty);

        var rendered = (presenter.PresentTerminal(call, terminal) ?? throw new InvalidOperationException())
            .Render(new ScrollbackRenderContext(32_768, new TerminalPalette(true)));

        _ = await Assert.That(rendered[0]).Contains("\u001b[32m");
        _ = await Assert.That(rendered[1]).IsEqualTo("  output");
    }

    [Test]
    [Arguments("Process exited with code 0 after 1s\n[stdout]\nout", "  Process exited with code 0 after 1s|  out")]
    [Arguments("Process exited with code 0 after 1s\n[stdout]\nout\n[stderr]\nerr", "  Process exited with code 0 after 1s|  [stdout]|  out|  [stderr]|  err")]
    [Arguments("Process exited with code 0 after 1s\n[stderr]\nerr", "  Process exited with code 0 after 1s|  [stderr]|  err")]
    public async Task Exec_process_output_omits_stdout_label_without_stderr(string result, string expected)
    {
        IToolPresenter presenter = new ExecCommandToolPresenter(TimeProvider.System, []);
        var call = new ToolCallPresentation("exec_command", "{\"command\":\"echo output\"}");
        var terminal = new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, result, string.Empty);

        var rendered = (presenter.PresentTerminal(call, terminal) ?? throw new InvalidOperationException()).Render(ScrollbackContext);

        _ = await Assert.That(string.Join('|', rendered.Skip(1))).IsEqualTo(expected);
    }

    [Test]
    [Arguments("rg", true)]
    [Arguments("  rg pattern", true)]
    [Arguments("grep\tpattern", true)]
    [Arguments("sed\rscript", true)]
    [Arguments("custom\nargument", true)]
    [Arguments("rgrep pattern", false)]
    [Arguments("sudo rg pattern", false)]
    [Arguments("echo x | rg pattern", false)]
    [Arguments("echo x; rg pattern", false)]
    public async Task Read_only_exec_prefix_matching_observes_shell_token_boundaries(string command, bool expectedReadOnly)
    {
        var palette = new TerminalPalette(true);
        IToolPresenter presenter = new ExecCommandToolPresenter(TimeProvider.System, ["rg", "grep", "sed", "custom"]);
        var arguments = "{\"command\":\"" + System.Text.Json.JsonEncodedText.Encode(command) + "\"}";
        var call = new ToolCallPresentation("exec_command", arguments);
        var live = presenter.PresentLive(call, 0);
        var terminal = presenter.PresentTerminal(
            call,
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "output", string.Empty))
            ?? throw new InvalidOperationException();

        _ = await Assert.That(live.Render(new LiveBufferRenderContext(32_768, palette)).Lines[0].Style)
            .IsEqualTo(expectedReadOnly ? palette.LiveMuted : palette.Marker);
        _ = await Assert.That(live.Render(new LiveBufferRenderContext(32_768, palette)).Lines[0].Text.Contains("running", StringComparison.Ordinal))
            .IsEqualTo(!expectedReadOnly);
        var expectedLines = expectedReadOnly ? command.Count(character => character == '\n') + 1 : 2;
        _ = await Assert.That(terminal.Render(new ScrollbackRenderContext(32_768, palette)).Count)
            .IsEqualTo(expectedLines);
    }

    [Test]
    [Arguments(ToolTerminalStatus.Errored, false, "", "permission denied", "permission denied")]
    [Arguments(ToolTerminalStatus.Succeeded, true, "Process exited with code 7 after 1s", "", "Process exited with code 7")]
    public async Task Matched_read_only_exec_preserves_failure_diagnostics(
        ToolTerminalStatus status,
        bool resultPresent,
        string result,
        string error,
        string expectedDiagnostic)
    {
        IToolPresenter presenter = new ExecCommandToolPresenter(TimeProvider.System, ["rg"]);
        var call = new ToolCallPresentation("exec_command", "{\"command\":\"rg pattern\"}");
        var terminal = presenter.PresentTerminal(
            call,
            new ToolTerminalPresentation(status, resultPresent, result, error))
            ?? throw new InvalidOperationException();
        var rendered = terminal.Render(ScrollbackContext);

        _ = await Assert.That(rendered[0]).StartsWith("✗ ");
        _ = await Assert.That(string.Join('\n', rendered)).Contains(expectedDiagnostic);
    }

    [Test]
    public async Task Write_stdin_shows_only_process_character_count_and_status()
    {
        IToolPresenter presenter = new WriteStdinToolPresenter();
        var registry = new ToolPresenterRegistry([presenter], new GenericToolPresenter());
        const string secretInput = "secret 🔒";
        const string secretResult = "private process output";
        var call = new ToolCallPresentation(
            "write_stdin",
            "{\"name\":\"build\",\"input\":\"secret \\uD83D\\uDD12\",\"yield_after_ms\":0}");
        var terminal = new ToolTerminalPresentation(
            ToolTerminalStatus.Succeeded,
            true,
            secretResult,
            string.Empty);

        var live = registry.PresentLive(call, 0).Render(LiveContext).Lines.Select(line => line.Text);
        var finished = registry.PresentTerminal(call, terminal)
            ?? throw new InvalidOperationException("Write stdin terminal presentation missing.");
        var rendered = finished.Render(ScrollbackContext);

        _ = await Assert.That(string.Join('\n', live)).Contains("write 8 chars to build");
        _ = await Assert.That(string.Join('\n', rendered)).Contains("write 8 chars to build");
        _ = await Assert.That(string.Join('\n', live)).DoesNotContain(secretInput);
        _ = await Assert.That(string.Join('\n', rendered)).DoesNotContain(secretInput);
        _ = await Assert.That(string.Join('\n', rendered)).DoesNotContain(secretResult);
        _ = await Assert.That(presenter.Metadata.RedactedInputFields).Contains("input");
        _ = await Assert.That(presenter.Metadata.SuppressTerminalDetails).IsTrue();
    }

    [Test]
    public async Task Write_stdin_is_nonthrowing_for_partial_arguments()
    {
        IToolPresenter presenter = new WriteStdinToolPresenter();
        var call = new ToolCallPresentation("write_stdin", "{\"name\":\"build\",\"input\":\"secret");
        var terminal = new ToolTerminalPresentation(ToolTerminalStatus.Errored, false, string.Empty, "secret error");

        var live = presenter.PresentLive(call, 0).Render(LiveContext).Lines[0].Text;
        var finished = (presenter.PresentTerminal(call, terminal) ?? throw new InvalidOperationException()).Render(ScrollbackContext);

        _ = await Assert.That(live).Contains("write input to process");
        _ = await Assert.That(string.Join('\n', finished)).Contains("write input to process");
        _ = await Assert.That(live).DoesNotContain("secret");
        _ = await Assert.That(string.Join('\n', finished)).DoesNotContain("secret error");
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
}
