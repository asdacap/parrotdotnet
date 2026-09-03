using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;
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
            new ToolCallPresentation("main", "exec_command", "{\"command\":\"compile\"}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "Process exited with code 7 after 1.23s", string.Empty),
            "$ compile",
            "✗ main: $ compile",
        ];
        yield return () =>
        [
            new InterruptProcessToolPresenter(),
            new ToolCallPresentation("main", "interrupt_process", "{\"name\":\"build\",\"signal\":15}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "Signal 15 sent to shell process 'build'.", string.Empty),
            "signal 15 build",
            "signal 15 build",
        ];
        yield return () =>
        [
            new InterruptProcessToolPresenter(),
            new ToolCallPresentation("main", "interrupt_process", "{\"name\":\"build\"}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "Process exited with code 2 after 0.42s", string.Empty),
            "signal 2 build",
            "✗ main: signal 2 build",
        ];
        yield return () =>
        [
            new InterruptProcessToolPresenter(),
            new ToolCallPresentation("main", "interrupt_process", "{\"name\":\"build\",\"signal\":null}"),
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "Signal 2 sent to shell process 'build'.", string.Empty),
            "signal 2 build",
            "signal 2 build",
        ];
    }

    [Test]
    public async Task Yielded_exec_process_defers_terminal_presentation()
    {
        var presenter = new ExecCommandToolPresenter(TimeProvider.System, []);
        var call = new ToolCallPresentation("main", "exec_command", "{\"command\":\"compile\"}");
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
            });

        _ = await Assert.That(presenter.PresentTerminal(call, terminal)).IsNull();
    }

    [Test]
    [Arguments("{\"command\":\"status\",\"name\":\"git\"}", "git")]
    [Arguments("{\"command\":\"status\"}", "shell-42")]
    public async Task Exec_output_does_not_imply_a_yielded_process(string arguments, string result)
    {
        var presenter = new ExecCommandToolPresenter(TimeProvider.System, []);
        var call = new ToolCallPresentation("main", "exec_command", arguments);
        var terminal = new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, result, string.Empty);

        var rendered = (presenter.PresentTerminal(call, terminal) ?? throw new InvalidOperationException()).Render(ScrollbackContext);

        _ = await Assert.That(rendered[0]).IsEqualTo("✓ main: $ status");
        _ = await Assert.That(string.Join('\n', rendered)).Contains(result);
        _ = await Assert.That(string.Join('\n', rendered)).DoesNotContain("process " + result + " running");
    }

    [Test]
    public async Task Spilled_nonzero_process_output_is_reported_as_a_failure()
    {
        var presenter = new ExecCommandToolPresenter(TimeProvider.System, []);
        var call = new ToolCallPresentation("main", "exec_command", "{\"command\":\"compile\"}");
        var outputPath = Path.GetFullPath(Path.Combine("state", "sessions", "session", "blob", "output"));
        var terminal = new ToolTerminalPresentation(
            ToolTerminalStatus.Succeeded,
            true,
            $"Process exited with code 7 after 3.14s\nTool output exceeded 64 KiB and was saved to {outputPath}.",
            string.Empty);

        var rendered = (presenter.PresentTerminal(call, terminal) ?? throw new InvalidOperationException()).Render(ScrollbackContext);

        _ = await Assert.That(terminal.ResolveProcessStatus()).IsEqualTo(ToolTerminalStatus.ReportedFailure);
        _ = await Assert.That(rendered[0]).IsEqualTo("✗ main: $ compile");
        _ = await Assert.That(string.Join('\n', rendered)).Contains($"saved to {outputPath}");
    }

    [Test]
    public async Task Multiline_exec_command_shows_five_lines_and_reports_omitted_lines()
    {
        var presenter = new ExecCommandToolPresenter(TimeProvider.System, []);
        var command = "python3 - <<'PY'\nfirst\nsecond\nthird\nfourth\nfifth\nPY";
        var call = new ToolCallPresentation(
            "main",
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
            "✓ main: $ python3 - <<'PY'\nfirst\nsecond\nthird\nfourth\n.. 2 lines truncated.");
    }

    [Test]
    public async Task Active_exec_process_shows_elapsed_runtime_across_animation_frames()
    {
        var timeProvider = new ControlledTimeProvider();
        var presenter = new ExecCommandToolPresenter(timeProvider, []);
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
        var presenter = new ExecCommandToolPresenter(TimeProvider.System, []);
        var call = new ToolCallPresentation("main", "exec_command", "{\"command\":\"echo output\"}");
        var terminal = new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "output", string.Empty);

        var rendered = (presenter.PresentTerminal(call, terminal) ?? throw new InvalidOperationException())
            .Render(new ScrollbackRenderContext(32_768, new TerminalPalette(true)));

        _ = await Assert.That(rendered[0]).Contains("\u001b[32m");
        _ = await Assert.That(rendered[1]).IsEqualTo("  output");
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
        var presenter = new ExecCommandToolPresenter(TimeProvider.System, ["rg", "grep", "sed", "custom"]);
        var arguments = "{\"command\":\"" + System.Text.Json.JsonEncodedText.Encode(command) + "\"}";
        var call = new ToolCallPresentation("main", "exec_command", arguments);
        var live = (ToolLiveValue)presenter.PresentLive(call, 0);
        var terminal = presenter.PresentTerminal(
            call,
            new ToolTerminalPresentation(ToolTerminalStatus.Succeeded, true, "output", string.Empty))
            ?? throw new InvalidOperationException();

        _ = await Assert.That(live.Report.Metadata.Style == ToolPresentationStyle.Muted).IsEqualTo(expectedReadOnly);
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
        var presenter = new ExecCommandToolPresenter(TimeProvider.System, ["rg"]);
        var call = new ToolCallPresentation("main", "exec_command", "{\"command\":\"rg pattern\"}");
        var terminal = presenter.PresentTerminal(
            call,
            new ToolTerminalPresentation(status, resultPresent, result, error))
            ?? throw new InvalidOperationException();
        var rendered = terminal.Render(ScrollbackContext);

        _ = await Assert.That(rendered[0]).StartsWith("✗ ");
        _ = await Assert.That(string.Join('\n', rendered)).Contains(expectedDiagnostic);
    }

    [Test]
    public async Task Activity_redacts_sensitive_chunks_and_tracks_names_by_agent_and_call_id()
    {
        var registry = new ToolPresenterRegistry([new WriteStdinToolPresenter()], new GenericToolPresenter());
        var activity = new EnhancedActivity(registry);
        var first = new Event
        {
            AgentSessionId = "main",
            ToolCallChunk = new ToolCallChunk
            {
                ToolCallId = "shared",
                ToolName = "write_stdin",
                ArgumentsFragment = "secret input",
            },
        };
        var continuation = new Event
        {
            AgentSessionId = "main",
            ToolCallChunk = new ToolCallChunk
            {
                ToolCallId = "shared",
                ArgumentsFragment = "more secret input",
            },
        };
        var otherAgent = new Event
        {
            AgentSessionId = "child",
            ToolCallChunk = new ToolCallChunk
            {
                ToolCallId = "shared",
                ArgumentsFragment = "unknown secret input",
            },
        };

        activity.Observe(first);
        var firstText = activity.Format(first, false);
        activity.Observe(continuation);
        var continuationText = activity.Format(continuation, false);
        activity.Observe(otherAgent);
        var otherText = activity.Format(otherAgent, false);

        _ = await Assert.That(firstText).IsEqualTo("tool call write_stdin: <redacted>");
        _ = await Assert.That(continuationText).IsEqualTo("tool call write_stdin: <redacted>");
        _ = await Assert.That(otherText).IsEqualTo("tool call: <redacted>");
        _ = await Assert.That(firstText + continuationText + otherText).DoesNotContain("secret input");
    }

    [Test]
    public async Task Activity_forgets_tracked_names_at_terminal_events_and_turn_completion()
    {
        var registry = new ToolPresenterRegistry([new ExecCommandToolPresenter(TimeProvider.System, [])], new GenericToolPresenter());
        var activity = new EnhancedActivity(registry);
        var named = Chunk("main", "call", "exec_command", "visible");
        activity.Observe(named);
        activity.Observe(new Event
        {
            AgentSessionId = "main",
            ToolFinished = new ToolFinished { ToolCallId = "call", ToolName = "exec_command" },
        });
        var afterTerminal = Chunk("main", "call", string.Empty, "terminal secret");
        var secondNamed = Chunk("main", "other", "exec_command", "visible");
        activity.Observe(secondNamed);
        activity.Observe(new Event { AgentSessionId = "main", TurnEnded = new TurnEnded() });
        var afterTurn = Chunk("main", "other", string.Empty, "turn secret");

        var terminalText = activity.Format(afterTerminal, false);
        var turnText = activity.Format(afterTurn, false);

        _ = await Assert.That(terminalText).IsEqualTo("tool call: <redacted>");
        _ = await Assert.That(turnText).IsEqualTo("tool call: <redacted>");
    }

    [Test]
    public async Task Write_stdin_shows_only_process_character_count_and_status()
    {
        var presenter = new WriteStdinToolPresenter();
        var registry = new ToolPresenterRegistry([presenter], new GenericToolPresenter());
        const string secretInput = "secret 🔒";
        const string secretResult = "private process output";
        var call = new ToolCallPresentation(
            "main",
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

        _ = await Assert.That(string.Join('\n', live)).Contains("main: write 8 chars to build");
        _ = await Assert.That(string.Join('\n', rendered)).Contains("main: write 8 chars to build");
        _ = await Assert.That(string.Join('\n', live)).DoesNotContain(secretInput);
        _ = await Assert.That(string.Join('\n', rendered)).DoesNotContain(secretInput);
        _ = await Assert.That(string.Join('\n', rendered)).DoesNotContain(secretResult);
        _ = await Assert.That(presenter.Metadata.RedactedInputFields).Contains("input");
        _ = await Assert.That(presenter.Metadata.SuppressTerminalDetails).IsTrue();
    }

    [Test]
    public async Task Write_stdin_is_nonthrowing_for_partial_arguments()
    {
        var presenter = new WriteStdinToolPresenter();
        var call = new ToolCallPresentation("main", "write_stdin", "{\"name\":\"build\",\"input\":\"secret");
        var terminal = new ToolTerminalPresentation(ToolTerminalStatus.Errored, false, string.Empty, "secret error");

        var live = presenter.PresentLive(call, 0).Render(LiveContext).Lines[0].Text;
        var finished = (presenter.PresentTerminal(call, terminal) ?? throw new InvalidOperationException()).Render(ScrollbackContext);

        _ = await Assert.That(live).Contains("main: write input to process");
        _ = await Assert.That(string.Join('\n', finished)).Contains("main: write input to process");
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

    private static Event Chunk(
        string agentSessionId,
        string toolCallId,
        string toolName,
        string arguments) => new()
        {
            AgentSessionId = agentSessionId,
            ToolCallChunk = new ToolCallChunk
            {
                ToolCallId = toolCallId,
                ToolName = toolName,
                ArgumentsFragment = arguments,
            },
        };

    private sealed class ControlledTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void SetElapsed(TimeSpan elapsed) => _timestamp = elapsed.Ticks;
    }
}
