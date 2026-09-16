using System.Text.Json;
using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Security;
using Parrot.State;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class ShellProcessInteractionTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "parrot-shell-interaction-tests", Guid.NewGuid().ToString("n"));

    public ShellProcessInteractionTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Test]
    [Arguments("{\"name\":\"process\",\"input\":\"hello\",\"unexpected\":true}", "process", "hello", null)]
    [Arguments("{\"name\":\"process\",\"input\":\"hello\",\"yield_after_ms\":125,\"future\":\"value\"}", "process", "hello", 125L)]
    public async Task Write_stdin_context_preserves_known_fields_with_unknown_fields(
        string argumentsJson,
        string expectedName,
        string expectedInput,
        long? expectedYieldAfterMilliseconds)
    {
        var input = JsonSerializer.Deserialize(
            argumentsJson,
            OmittedAgentProcessToolJsonContext.Default.WriteStdinToolInput)
            ?? throw new InvalidOperationException("Expected stdin input.");

        _ = await Assert.That(input).IsNotNull();
        _ = await Assert.That(input.Name).IsEqualTo(expectedName);
        _ = await Assert.That(input.Text).IsEqualTo(expectedInput);
        _ = await Assert.That(input.YieldAfterMilliseconds).IsEqualTo(expectedYieldAfterMilliseconds);
    }

    [Test]
    [Arguments("{\"name\":\"process\",\"unknown\":true}", "process", null)]
    [Arguments("{\"name\":\"process\",\"signal\":17,\"future\":\"value\"}", "process", 17)]
    public async Task Interrupt_context_preserves_known_fields_with_unknown_fields(
        string argumentsJson,
        string expectedName,
        int? expectedSignal)
    {
        var input = JsonSerializer.Deserialize(
            argumentsJson,
            OmittedAgentProcessToolJsonContext.Default.InterruptProcessToolInput)
            ?? throw new InvalidOperationException("Expected interrupt input.");

        _ = await Assert.That(input).IsNotNull();
        _ = await Assert.That(input.Name).IsEqualTo(expectedName);
        _ = await Assert.That(input.Signal).IsEqualTo(expectedSignal);
    }

    [Test]
    public async Task Pseudo_terminal_writes_input_and_consumes_each_output_segment_once(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var lifetime = new CancellationTokenSource();
        using var events = new EventBroker();
        using var database = SessionDatabase.Open(":memory:");
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".state"),
                Path.Combine(_workspace, ".config"),
                Path.Combine(_workspace, ".data")),
            UserSessionId.Parse($"session-{Guid.NewGuid():n}"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        await using var agent = CreateAgent(events, database, resources.AgentScratch("agent").BlobDirectory, lifetime.Token);
        await using var owner = new ShellProcessOwner(
            AgentIdentity.Main(agent.SessionId, agent.Name, TestModels.PromptTemplates),
            resources,
            new AgentPathEnvironment(resources, resources.AgentScratch(agent.SessionId)),
            new ProcessRunner(CreateSandboxPassThrough()),
            TestDiagnosticLog.Instance,
            lifetime.Token);
        var process = owner.StartUnattributed(
            "interactive",
            "printf first; IFS= read -r line; printf 'received:%s' \"$line\"; exit 7",
            ProcessEnvironmentOverrides.Empty,
            agent,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            ShellProcessTerminalMode.PseudoTerminal);

        var initial = await process.Wait(TimeSpan.FromMilliseconds(500), cancellationToken);
        var completed = await owner.WriteStdin(
            "interactive",
            "hello\n",
            TimeSpan.FromSeconds(2),
            cancellationToken);

        _ = await Assert.That(initial.Running).IsTrue();
        _ = await Assert.That(initial.YieldedProcess?.StdoutPath).IsNull();
        _ = await Assert.That(initial.YieldedProcess?.StderrPath).IsNull();
        _ = await Assert.That(initial.Output).Contains("first");
        _ = await Assert.That(initial.Output).DoesNotContain("READY");
        _ = await Assert.That(completed.Running).IsFalse();
        var result = completed.Result ?? throw new InvalidOperationException("Missing completed process result.");
        _ = await Assert.That(result.ExitCode).IsEqualTo(7);
        _ = await Assert.That(result.Stdout).Contains("received:hello");
        _ = await Assert.That(result.Stdout).DoesNotContain("first");
        _ = await Assert.That(result.Stdout).DoesNotContain("READY");

        await owner.Settle();
    }

    [Test]
    [Arguments(ShellProcessTerminalMode.Pipe)]
    [Arguments(ShellProcessTerminalMode.PseudoTerminal)]
    public async Task Session_paths_use_native_shell_expansion_and_survive_yield(
        ShellProcessTerminalMode terminalMode,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var workspace = Path.Combine(_workspace, "space ' quote $dollar; $(printf injected) `literal` [glob]");
        _ = Directory.CreateDirectory(workspace);
        using var lifetime = new CancellationTokenSource();
        using var events = new EventBroker();
        using var database = SessionDatabase.Open(":memory:");
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(workspace, ".state"),
                Path.Combine(workspace, ".config"),
                Path.Combine(workspace, ".data")),
            UserSessionId.Parse($"session-{Guid.NewGuid():n}"),
            ProjectWorkspace.FromLaunchDirectory(workspace));
        await using var agent = CreateAgent(events, database, resources.AgentScratch("agent").BlobDirectory, lifetime.Token);
        var scratch = resources.AgentScratch(agent.SessionId);
        await using var owner = new ShellProcessOwner(
            agent.Identity,
            resources,
            new AgentPathEnvironment(resources, scratch),
            new ProcessRunner(CreateSandboxPassThrough()),
            TestDiagnosticLog.Instance,
            lifetime.Token);
        var securityProfile = SecurityProfile.Compose(readOnly: false, [], [], []);
        const string command = """
            while [ ! -f "$WORKDIR/release" ]; do sleep 0.02; done
            printf '<%s>' "$PWD" "$WORKDIR" "${SCRATCH_DIR}" "$AGENT_SCRATCH_DIR" "${AGENT_HISTORY_DIR}"
            printf '<%s>' '$SCRATCH_DIR' '${AGENT_HISTORY_DIR}'
            printf work > "$WORKDIR/work file.txt"
            printf shared > "${SCRATCH_DIR}/shared file.txt"
            printf agent > "$AGENT_SCRATCH_DIR/agent file.txt"
            printf history > "${AGENT_HISTORY_DIR}/history file.txt"
            cd "$SCRATCH_DIR" || exit
            printf '<%s>' "$WORKDIR"
            """;
        var process = owner.StartUnattributed(
            "paths",
            command,
            ProcessEnvironmentOverrides.Empty,
            agent,
            securityProfile,
            terminalMode);
        var yielded = await process.Wait(TimeSpan.Zero, cancellationToken);
        _ = await Assert.That(yielded.Running).IsTrue();

        var overridden = owner.StartUnattributed(
            "override",
            "printf '<%s>' \"$WORKDIR\" \"$SCRATCH_DIR\" \"$AGENT_SCRATCH_DIR\" \"$AGENT_HISTORY_DIR\"",
            new ProcessEnvironmentOverrides(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["AGENT_SCRATCH_DIR"] = "literal ${WORKDIR} $(printf injected)",
            }),
            agent,
            securityProfile,
            terminalMode);
        var overrideResult = await overridden.Wait(null, cancellationToken);
        _ = await Assert.That(overrideResult.Result?.ExitCode).IsEqualTo(0);
        _ = await Assert.That(overrideResult.Result?.Stdout)
            .IsEqualTo($"<{workspace}><{resources.ScratchRootDirectory}><literal ${{WORKDIR}} $(printf injected)><{Path.GetDirectoryName(scratch.HistoryPath)}>");

        await File.WriteAllTextAsync(Path.Combine(workspace, "release"), string.Empty, cancellationToken);
        var completed = await owner.Claim("paths").Wait(null, cancellationToken);
        _ = await Assert.That(completed.Result?.ExitCode).IsEqualTo(0);
        _ = await Assert.That(completed.Result?.Stdout)
            .IsEqualTo($"<{workspace}><{workspace}><{resources.ScratchRootDirectory}><{scratch.Root}><{Path.GetDirectoryName(scratch.HistoryPath)}><$SCRATCH_DIR><${{AGENT_HISTORY_DIR}}><{workspace}>");
        _ = await Assert.That(await File.ReadAllTextAsync(Path.Combine(workspace, "work file.txt"), cancellationToken)).IsEqualTo("work");
        _ = await Assert.That(await File.ReadAllTextAsync(Path.Combine(resources.ScratchRootDirectory, "shared file.txt"), cancellationToken)).IsEqualTo("shared");
        _ = await Assert.That(await File.ReadAllTextAsync(Path.Combine(scratch.Root, "agent file.txt"), cancellationToken)).IsEqualTo("agent");
        _ = await Assert.That(await File.ReadAllTextAsync(Path.Combine(scratch.Root, "history file.txt"), cancellationToken)).IsEqualTo("history");
        await owner.Settle();
    }

    [Test]
    public async Task Completed_spilled_suffix_survives_prompt_transcript_cleanup(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var lifetime = new CancellationTokenSource();
        using var events = new EventBroker();
        using var database = SessionDatabase.Open(":memory:");
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".state"),
                Path.Combine(_workspace, ".config"),
                Path.Combine(_workspace, ".data")),
            UserSessionId.Parse($"session-{Guid.NewGuid():n}"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        await using var agent = CreateAgent(events, database, resources.AgentScratch("agent").BlobDirectory, lifetime.Token);
        await using var owner = new ShellProcessOwner(
            AgentIdentity.Main(agent.SessionId, agent.Name, TestModels.PromptTemplates),
            resources,
            new AgentPathEnvironment(resources, resources.AgentScratch(agent.SessionId)),
            new ProcessRunner(CreateSandboxPassThrough()),
            TestDiagnosticLog.Instance,
            lifetime.Token);
        var process = owner.StartUnattributed(
            "spill",
            "printf prefix; sleep 0.5; dd if=/dev/zero bs=70000 count=1 2>/dev/null | tr '\\0' x",
            ProcessEnvironmentOverrides.Empty,
            agent,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            ShellProcessTerminalMode.PseudoTerminal);
        var initial = await process.Wait(TimeSpan.FromMilliseconds(100), cancellationToken);
        var completed = await owner.Claim("spill").Wait(null, cancellationToken);
        var result = completed.Result ?? throw new InvalidOperationException("Missing completed process result.");

        _ = await Assert.That(initial.Running).IsTrue();
        _ = await Assert.That(result.Spilled).IsTrue();
        await WaitForNoTranscriptSpools(resources.AgentScratch(agent.SessionId).BlobDirectory, cancellationToken);
        _ = await Assert.That(File.Exists(result.BlobPath)).IsTrue();
        _ = await Assert.That(Directory.EnumerateFiles(resources.AgentScratch(agent.SessionId).BlobDirectory, ".process-*.tmp")).IsEmpty();
        var durable = await File.ReadAllTextAsync(result.BlobPath, cancellationToken);
        _ = await Assert.That(durable).Contains("[stdout]\n");
        _ = await Assert.That(durable).DoesNotContain("prefix");
        var stdout = durable[(durable.IndexOf("[stdout]\n", StringComparison.Ordinal) + "[stdout]\n".Length)..];
        _ = await Assert.That(stdout.Length).IsEqualTo(70000);
        _ = await Assert.That(stdout.All(value => value == 'x')).IsTrue();

        await owner.Settle();
        _ = await Assert.That(File.Exists(result.BlobPath)).IsTrue();
    }

    [Test]
    public async Task Empty_input_polls_only_output_after_the_prior_cursor(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var lifetime = new CancellationTokenSource();
        using var events = new EventBroker();
        using var database = SessionDatabase.Open(":memory:");
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".state"),
                Path.Combine(_workspace, ".config"),
                Path.Combine(_workspace, ".data")),
            UserSessionId.Parse($"session-{Guid.NewGuid():n}"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        await using var agent = CreateAgent(events, database, resources.AgentScratch("agent").BlobDirectory, lifetime.Token);
        await using var owner = new ShellProcessOwner(
            AgentIdentity.Main(agent.SessionId, agent.Name, TestModels.PromptTemplates),
            resources,
            new AgentPathEnvironment(resources, resources.AgentScratch(agent.SessionId)),
            new ProcessRunner(CreateSandboxPassThrough()),
            TestDiagnosticLog.Instance,
            lifetime.Token);
        var process = owner.StartUnattributed(
            "poll",
            "printf first; sleep 0.2; printf second; sleep 30",
            ProcessEnvironmentOverrides.Empty,
            agent,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            ShellProcessTerminalMode.PseudoTerminal);

        var initial = await process.Wait(TimeSpan.FromMilliseconds(100), cancellationToken);
        var poll = await owner.WriteStdin("poll", string.Empty, TimeSpan.FromMilliseconds(500), cancellationToken);

        _ = await Assert.That(initial.Running).IsTrue();
        _ = await Assert.That(initial.Output).Contains("first");
        _ = await Assert.That(poll.Running).IsTrue();
        _ = await Assert.That(poll.Output).Contains("second");
        _ = await Assert.That(poll.Output).DoesNotContain("first");

        var claimed = owner.Claim("poll");
        await claimed.SendSignal(new ProcessSignal(9), cancellationToken);
        await lifetime.CancelAsync();
    }

    [Test]
    public async Task Pipe_process_exposes_stable_live_stream_files_before_completion(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var lifetime = new CancellationTokenSource();
        using var events = new EventBroker();
        using var database = SessionDatabase.Open(":memory:");
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".state"),
                Path.Combine(_workspace, ".config"),
                Path.Combine(_workspace, ".data")),
            UserSessionId.Parse($"session-{Guid.NewGuid():n}"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        await using var agent = CreateAgent(events, database, resources.AgentScratch("agent").BlobDirectory, lifetime.Token);
        await using var owner = new ShellProcessOwner(
            AgentIdentity.Main(agent.SessionId, agent.Name, TestModels.PromptTemplates),
            resources,
            new AgentPathEnvironment(resources, resources.AgentScratch(agent.SessionId)),
            new ProcessRunner(CreateSandboxPassThrough()),
            TestDiagnosticLog.Instance,
            lifetime.Token);
        var process = owner.StartUnattributed(
            "live-pipe",
            """printf before; printf problem >&2; while [ ! -f "$WORKDIR/release" ]; do sleep 0.02; done; printf after""",
            ProcessEnvironmentOverrides.Empty,
            agent,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            ShellProcessTerminalMode.Pipe);

        var initial = await process.Wait(TimeSpan.Zero, cancellationToken);
        var (stdoutPath, stderrPath) = LivePaths(initial.Output);
        var yieldedProcess = initial.YieldedProcess
            ?? throw new InvalidOperationException("The pipe process did not yield.");
        _ = await Assert.That(yieldedProcess.StdoutPath).IsEqualTo(stdoutPath);
        _ = await Assert.That(yieldedProcess.StderrPath).IsEqualTo(stderrPath);
        _ = await Assert.That(Path.IsPathFullyQualified(stdoutPath)).IsTrue();
        _ = await Assert.That(Path.IsPathFullyQualified(stderrPath)).IsTrue();
        await WaitForLiveText(stdoutPath, "before", cancellationToken);
        await WaitForLiveText(stderrPath, "problem", cancellationToken);
        var repeated = await owner.Claim("live-pipe").Wait(TimeSpan.Zero, cancellationToken);

        _ = await Assert.That(initial.Running).IsTrue();
        _ = await Assert.That(repeated.Running).IsTrue();
        _ = await Assert.That(repeated.Output).IsEqualTo(initial.Output);
        _ = await Assert.That(repeated.YieldedProcess?.StdoutPath).IsEqualTo(stdoutPath);
        _ = await Assert.That(repeated.YieldedProcess?.StderrPath).IsEqualTo(stderrPath);
        _ = await Assert.That(File.Exists(stdoutPath)).IsTrue();
        _ = await Assert.That(File.Exists(stderrPath)).IsTrue();

        await File.WriteAllTextAsync(Path.Combine(_workspace, "release"), string.Empty, cancellationToken);
        var completed = await owner.Claim("live-pipe").Wait(null, cancellationToken);
        _ = await Assert.That(completed.Result?.Stdout).IsEqualTo("beforeafter");
        _ = await Assert.That(completed.Result?.Stderr).IsEqualTo("problem");
        _ = await Assert.That(await File.ReadAllTextAsync(stdoutPath, cancellationToken)).IsEqualTo("beforeafter");
        _ = await Assert.That(await File.ReadAllTextAsync(stderrPath, cancellationToken)).IsEqualTo("problem");

        await owner.Settle();
        _ = await Assert.That(File.Exists(stdoutPath)).IsTrue();
        _ = await Assert.That(File.Exists(stderrPath)).IsTrue();
    }

    [Test]
    public async Task Cancelled_wait_releases_the_exclusive_claim(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var lifetime = new CancellationTokenSource();
        using var events = new EventBroker();
        using var database = SessionDatabase.Open(":memory:");
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".state"),
                Path.Combine(_workspace, ".config"),
                Path.Combine(_workspace, ".data")),
            UserSessionId.Parse($"session-{Guid.NewGuid():n}"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        await using var agent = CreateAgent(events, database, resources.AgentScratch("agent").BlobDirectory, lifetime.Token);
        await using var owner = new ShellProcessOwner(
            AgentIdentity.Main(agent.SessionId, agent.Name, TestModels.PromptTemplates),
            resources,
            new AgentPathEnvironment(resources, resources.AgentScratch(agent.SessionId)),
            new ProcessRunner(CreateSandboxPassThrough()),
            TestDiagnosticLog.Instance,
            lifetime.Token);
        var process = owner.StartUnattributed(
            "claimed",
            "sleep 30",
            ProcessEnvironmentOverrides.Empty,
            agent,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            ShellProcessTerminalMode.Pipe);
        _ = await process.Wait(TimeSpan.Zero, cancellationToken);
        var claimed = owner.Claim("claimed");

        _ = await Assert.That(() => owner.Claim("claimed"))
            .Throws<InvalidOperationException>();
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        _ = await Assert.That(async () => await claimed.Wait(null, canceled.Token))
            .Throws<OperationCanceledException>();

        var reclaimed = owner.Claim("claimed");
        await reclaimed.SendSignal(new ProcessSignal(9), cancellationToken);
        await owner.Settle();
    }

    private static (string Stdout, string Stderr) LivePaths(string output)
    {
        const string stdoutPrefix = "stdout: ";
        const string stderrPrefix = "stderr: ";
        var stdout = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(line => line.StartsWith(stdoutPrefix, StringComparison.Ordinal));
        var stderr = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .SingleOrDefault(line => line.StartsWith(stderrPrefix, StringComparison.Ordinal));

        if (stdout is null || stderr is null)
        {
            throw new InvalidOperationException("Pipe process did not report live output paths.");
        }

        return (stdout[stdoutPrefix.Length..], stderr[stderrPrefix.Length..]);
    }

    private static async Task WaitForLiveText(
        string path, string expected, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                var text = await reader.ReadToEndAsync(cancellationToken);
                if (text.Contains(expected, StringComparison.Ordinal))
                {
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }

        throw new TimeoutException($"Live pipe output '{expected}' was not observed.");
    }

    private static async Task WaitForNoTranscriptSpools(
        string directory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (!Directory.EnumerateFiles(directory, ".process-*.tmp").Any())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
        }
    }

    private IAgentSession CreateAgent(
        IEventBroker events,
        SessionDatabase database,
        string blobDirectory,
        CancellationToken lifetime)
    {
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        var repository = new EventRepository(database);
        var identity = AgentIdentity.Main("agent", "agent", TestModels.PromptTemplates);
        using var dependencies = TestModels.Dependencies(identity, events, repository, lifetime);
        return new AgentSession(identity, AgentSessionParentScope.Root(), new ModelSelector(model.Selector), TestModels.Route(model), events, repository, [], TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, _workspace, _workspace), new ToolOutputBlobStore(blobDirectory), new AgentOutputFile(blobDirectory), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(TestDiagnosticLog.Instance, "agent-test", null), new ContextCadence(), TestModels.PromptTemplates, dependencies.ChildQuestions, dependencies.ExitReminder, dependencies.Profile, new TestCompletionCallbacksFixture(dependencies.ChildQuestions, dependencies.ActiveWorkReminder, dependencies.ExitReminder, repository, events).Callbacks, new SecurityProfileTestFixture(SecurityProfile.Compose(readOnly: false, [], [], [])).Security, dependencies.Status, new AgentSessionActivity(TimeProvider.System), TestDiagnosticLog.Instance, lifetime);
    }

    private string CreateSandboxPassThrough()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        var path = Path.Combine(_workspace, $"sandbox-{Guid.NewGuid():n}");
        var script = "#!/bin/sh\nhelper=\nwhile [ \"$1\" != \"--\" ]; do\n"
            + "  if [ \"$1\" = \"--chdir\" ]; then shift; cd \"$1\" || exit; "
            + "elif [ \"$1\" = \"--setenv\" ]; then export \"$2=$3\"; shift 2; "
            + "elif [ \"$1\" = \"--ro-bind\" ] && [ \"$2\" = \"$3\" ] "
            + "&& [ \"$(basename \"$2\")\" = \"parrot-pty-attach\" ]; "
            + "then helper=$2; shift 2; fi\n  shift\ndone\nshift\n"
            + "if [ \"$1\" = \"$helper\" ] && [ -n \"$helper\" ]; then shift; exec \"$helper\" \"$@\"; fi\n"
            + "exec \"$@\"\n";
        File.WriteAllText(path, script);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using (var scriptFile = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            scriptFile.Flush(flushToDisk: true);
        }

        return path;
    }
}
