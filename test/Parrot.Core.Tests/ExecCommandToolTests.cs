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

internal sealed class ExecCommandToolTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "parrot-exec-tool-tests", Guid.NewGuid().ToString("n"));

    public ExecCommandToolTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Test]
    public async Task Arguments_and_process_result_are_reported(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var events = new EventBroker();
        using var database = SessionDatabase.Open(":memory:");
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        var identity = AgentIdentity.Main("session", string.Empty, TestModels.PromptTemplates);
        var repository = new EventRepository(database);
        using var dependencies = TestModels.Dependencies(identity, events, repository, CancellationToken.None);
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".state"),
                Path.Combine(_workspace, ".config"),
                Path.Combine(_workspace, ".data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        var scratch = resources.AgentScratch(identity.SessionId);
        var session = new AgentSession(identity, AgentSessionParentScope.Root(), new ModelSelector(model.Selector), TestModels.Route(model), events, repository, [], TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, _workspace, _workspace), new ToolOutputBlobStore(scratch.BlobDirectory), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), TestModels.PromptTemplates, dependencies.ChildQuestions, dependencies.ActiveWorkReminder, dependencies.ExitReminder, dependencies.Profile, SecurityProfileTestFactory.Create(SecurityProfile.Compose(readOnly: false, [], [], [])), dependencies.Status, dependencies.ChildRegistry, dependencies.Queues, new AgentSessionActivity(TimeProvider.System), CancellationToken.None);
        using var inventory = new ShellProcessInventory();
        var processes = new ShellProcessOwner(
            session.SessionId,
            resources,
            scratch,
            new ProcessRunner(CreateSandboxPassThrough(_workspace)),
            inventory,
            CancellationToken.None);
        var securityProfile = SecurityProfile.Compose(readOnly: false, [], [], []);
        var tool = new ExecCommandTool(processes, session);
        var selection = new AgentTurnSelection(
            new ModelSelector(model.Selector),
            TestModels.Resolve(model),
            TestModels.Profile(),
            securityProfile);
        var factoryTool = new ExecCommandToolFactory(processes).Create(session);
        var writeStdinFactoryTool = new WriteStdinToolFactory(processes).Create(session);
        _ = await Assert.That(factoryTool.Name).IsEqualTo("exec_command");
        _ = await Assert.That(writeStdinFactoryTool.Name).IsEqualTo("write_stdin");

        var result = await Execute(tool, """{"command":"printf out; printf err >&2; exit 7"}""", selection, cancellationToken);
        var missing = await Execute(tool, "{}", selection, cancellationToken);
        var malformed = await Execute(tool, "[]", selection, cancellationToken);
        var invalidEnvironment = await Execute(tool, """{"command":"true","env":{"VALUE":1}}""", selection, cancellationToken);
        var malformedEnvironment = await Execute(tool, """{"command":"true","env":[]}""", selection, cancellationToken);
        var environment = await Execute(tool, """{"command":"printf '%s' \"$COMMAND_VALUE\"","env":{"COMMAND_VALUE":"available"}}""", selection, cancellationToken);
        var emptyEnvironment = await Execute(tool, """{"command":"printf '<%s>' \"$COMMAND_VALUE\"","env":{"COMMAND_VALUE":""}}""", selection, cancellationToken);
        var inheritedPath = await Execute(tool, """{"command":"printf '%s' \"$PATH\""}""", selection, cancellationToken);
        var emptyEnvironmentName = await Execute(tool, """{"command":"true","env":{"":"value"}}""", selection, cancellationToken);
        var invalidEnvironmentName = await Execute(tool, """{"command":"true","env":{"INVALID=NAME":"value"}}""", selection, cancellationToken);
        var invalidEnvironmentValue = await Execute(tool, """{"command":"true","env":{"VALUE":"\u0000"}}""", selection, cancellationToken);

        _ = await Assert.That(result.Text).StartsWith("Process exited with code 7 after ");
        _ = await Assert.That(result.Text).EndsWith("s\n[stdout]\nout\n[stderr]\nerr");
        _ = await Assert.That(missing.Text).IsEqualTo("error: Tool arguments require a string 'command'.");
        _ = await Assert.That(malformed.Text).IsEqualTo("error: Tool arguments require a string 'command'.");
        _ = await Assert.That(invalidEnvironment.Text)
            .IsEqualTo("error: Tool argument 'env' must contain only string values.");
        _ = await Assert.That(malformedEnvironment.Text)
            .IsEqualTo("error: Tool argument 'env' must be an object containing string values.");
        _ = await Assert.That(environment.Text).StartsWith("Process exited with code 0 after ");
        _ = await Assert.That(environment.Text).EndsWith("s\n[stdout]\navailable");
        _ = await Assert.That(emptyEnvironment.Text).StartsWith("Process exited with code 0 after ");
        _ = await Assert.That(emptyEnvironment.Text).EndsWith("s\n[stdout]\n<>");
        _ = await Assert.That(inheritedPath.Text).StartsWith("Process exited with code 0 after ");
        _ = await Assert.That(inheritedPath.Text)
            .EndsWith($"s\n[stdout]\n{Environment.GetEnvironmentVariable("PATH")}");
        _ = await Assert.That(emptyEnvironmentName.Text)
            .IsEqualTo("error: Tool argument 'env' contains an invalid environment value.");
        _ = await Assert.That(invalidEnvironmentName.Text)
            .IsEqualTo("error: Tool argument 'env' contains an invalid environment value.");
        _ = await Assert.That(invalidEnvironmentValue.Text)
            .IsEqualTo("error: Tool argument 'env' contains an invalid environment value.");

        var spilled = await Execute(tool, """{"command":"awk 'BEGIN { for (i = 0; i < 70000; i++) printf \"x\" }'"}""", selection, cancellationToken);
        const string spillNotice = "\nTool output exceeded 64 KiB and was saved to ";
        const string spilledSuffix = ".";
        var noticeStart = spilled.Text.IndexOf(spillNotice, StringComparison.Ordinal);
        _ = await Assert.That(spilled.Text).StartsWith("Process exited with code 0 after ");
        _ = await Assert.That(noticeStart).IsGreaterThan(0);
        var spilledPath = spilled.Text[(noticeStart + spillNotice.Length)..^spilledSuffix.Length];
        _ = await Assert.That(Path.IsPathFullyQualified(spilledPath)).IsTrue();
        _ = await Assert.That(Path.GetDirectoryName(spilledPath)).IsEqualTo(scratch.BlobDirectory);

        var yielded = await Execute(tool, """{"command":"sleep 0.05; printf '%s' \"$LATER_VALUE\"","env":{"LATER_VALUE":"later"},"name":"later","yield_after_ms":0}""", selection, cancellationToken);
        var waited = (await processes.Claim("later").Wait(null, cancellationToken)).Format();
        var reusedAfterCompletion = await Execute(tool, """{"command":"printf reused","name":"later"}""", selection, cancellationToken);
        var defaultSignalProcess = await Execute(
            tool,
            """{"command":"sleep 30","name":"default-signal","yield_after_ms":0}""",
            selection,
            cancellationToken);
        var defaultSignaled = await Execute(
            new InterruptProcessTool(processes),
            """{"name":"default-signal"}""",
            selection,
            cancellationToken);
        var defaultCompletion = (await processes.Claim("default-signal").Wait(null, cancellationToken)).Format();
        var running = await Execute(tool, """{"command":"sleep 30","name":"running","yield_after_ms":0}""", selection, cancellationToken);
        var runningDuplicate = await Execute(tool, """{"command":"true","name":"running"}""", selection, cancellationToken);
        var signaled = await Execute(
            new InterruptProcessTool(processes),
            """{"name":"running","signal":17}""",
            selection,
            cancellationToken);
        var killed = await Execute(new InterruptProcessTool(processes), """{"name":"running","signal":9}""", selection, cancellationToken);
        var waitedAfterKill = (await processes.Claim("running").Wait(null, cancellationToken)).Format();
        var reusedAfterKill = await Execute(tool, """{"command":"printf restarted","name":"running"}""", selection, cancellationToken);
        var invalidLowSignal = await Execute(
            new InterruptProcessTool(processes),
            """{"name":"running","signal":0}""",
            selection,
            cancellationToken);
        var invalidHighSignal = await Execute(
            new InterruptProcessTool(processes),
            """{"name":"running","signal":65}""",
            selection,
            cancellationToken);
        var malformedSignal = await Execute(
            new InterruptProcessTool(processes),
            """{"name":"running","signal":"SIGINT"}""",
            selection,
            cancellationToken);

        _ = await Assert.That(yielded.Text).StartsWith("later\nProcess output is streaming.\nstdout: ");
        _ = await Assert.That(yielded.Text).Contains("\nstderr: ");
        _ = await Assert.That(yielded.YieldedProcess?.StdoutPath).IsNotNull();
        _ = await Assert.That(yielded.YieldedProcess?.StderrPath).IsNotNull();
        _ = await Assert.That(waited).StartsWith("Process exited with code 0 after ");
        _ = await Assert.That(waited).EndsWith("s\n[stdout]\nlater");
        _ = await Assert.That(reusedAfterCompletion.Text).StartsWith("Process exited with code 0 after ");
        _ = await Assert.That(reusedAfterCompletion.Text).EndsWith("s\n[stdout]\nreused");
        _ = await Assert.That(defaultSignalProcess.Text)
            .StartsWith("default-signal\nProcess output is streaming.\nstdout: ");
        _ = await Assert.That(defaultSignaled.Text).IsEqualTo("Signal 2 sent to shell process 'default-signal'.");
        _ = await Assert.That(defaultCompletion).StartsWith("Process exited with code ");
        _ = await Assert.That(running.Text)
            .StartsWith("running\nProcess output is streaming.\nstdout: ");
        _ = await Assert.That(runningDuplicate.Text)
            .IsEqualTo("error: Shell process name 'running' is already reserved.");
        _ = await Assert.That(signaled.Text).IsEqualTo("Signal 17 sent to shell process 'running'.");
        _ = await Assert.That(killed.Text).IsEqualTo("Signal 9 sent to shell process 'running'.");
        _ = await Assert.That(waitedAfterKill).StartsWith("Process exited with code ");
        _ = await Assert.That(reusedAfterKill.Text).StartsWith("Process exited with code 0 after ");
        _ = await Assert.That(reusedAfterKill.Text).EndsWith("s\n[stdout]\nrestarted");
        _ = await Assert.That(invalidLowSignal.Text)
            .IsEqualTo("error: Tool argument 'signal' must be between 1 and 64.");
        _ = await Assert.That(invalidHighSignal.Text)
            .IsEqualTo("error: Tool argument 'signal' must be between 1 and 64.");
        _ = await Assert.That(malformedSignal.Text).IsEqualTo("error: Tool argument 'signal' must be an integer.");
    }

    private static Task<ToolExecutionResult> Execute(
        ExecCommandTool tool,
        string argumentsJson,
        AgentTurnSelection selection,
        CancellationToken cancellationToken) =>
        tool.Execute(new ToolInvocation("call-id", argumentsJson), selection, cancellationToken);

    private static Task<ToolExecutionResult> Execute(
        InterruptProcessTool tool,
        string argumentsJson,
        AgentTurnSelection selection,
        CancellationToken cancellationToken) =>
        tool.Execute(new ToolInvocation("call-id", argumentsJson), selection, cancellationToken);

    private static string CreateSandboxPassThrough(string workspace)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        var path = Path.Combine(workspace, "sandbox");
        var script = "#!/bin/sh\nwhile [ \"$1\" != \"--\" ]; do\n"
            + "  if [ \"$1\" = \"--chdir\" ]; then shift; cd \"$1\" || exit; "
            + "elif [ \"$1\" = \"--setenv\" ]; then export \"$2=$3\"; shift 2; fi\n"
            + "  shift\ndone\nshift\nexec \"$@\"\n";
        File.WriteAllText(path, script);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
