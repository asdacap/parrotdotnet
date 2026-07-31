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
        var session = new AgentSession(
            AgentIdentity.Main("session", string.Empty),
            new ModelSelector(model.Selector),
            TestModels.Route(model),
            events,
            new EventRepository(database),
            [],
            TestModels.PromptProvider(_workspace, _workspace),
            new TodoCollection("session", new EventRepository(database), events),
            new ToolOutputBlobStore(Path.Combine(_workspace, "blob")),
            new Compactor(120_000),
            activeWorkReminder: null,
            null,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            null,
            null,
            null,
            CancellationToken.None);
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".state"),
                Path.Combine(_workspace, ".config"),
                Path.Combine(_workspace, ".data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        using var inventory = new ShellProcessInventory();
        var processes = new ShellProcessOwner(
            session.SessionId,
            resources,
            new ProcessRunner(CreateSandboxPassThrough(_workspace)),
            inventory,
            CancellationToken.None);
        var securityProfile = SecurityProfile.Compose(readOnly: false, [], [], []);
        var tool = new ExecCommandTool(processes, session, securityProfile, session.WriteGrants);
        var selection = new AgentTurnSelection(
            new ModelSelector(model.Selector),
            TestModels.Resolve(model),
            null,
            securityProfile);
        var factoryTool = new ExecCommandToolFactory(processes).Create(session, selection);
        var writeStdinFactoryTool = new WriteStdinToolFactory(processes).Create(session, selection);
        _ = await Assert.That(factoryTool.Name).IsEqualTo("exec_command");
        _ = await Assert.That(factoryTool.ParametersJson).IsEqualTo(ExecCommandTool.Input.Descriptor);
        _ = await Assert.That(writeStdinFactoryTool.Name).IsEqualTo("write_stdin");
        _ = await Assert.That(writeStdinFactoryTool.ParametersJson).IsEqualTo(WriteStdinTool.Input.Descriptor);
        using var schema = JsonDocument.Parse(tool.ParametersJson);
        var schemaRoot = schema.RootElement;
        var properties = schemaRoot.GetProperty("properties");
        var environmentSchema = properties.GetProperty("env");
        var terminalSchema = properties.GetProperty("tty");
        _ = await Assert.That(schemaRoot.GetProperty("additionalProperties").GetBoolean()).IsFalse();
        _ = await Assert.That(terminalSchema.GetProperty("type").GetString()).IsEqualTo("boolean");
        _ = await Assert.That(terminalSchema.GetProperty("default").GetBoolean()).IsFalse();
        _ = await Assert.That(environmentSchema.GetProperty("type").GetString()).IsEqualTo("object");
        _ = await Assert.That(environmentSchema.GetProperty("additionalProperties").GetProperty("type").GetString())
            .IsEqualTo("string");

        var result = await Execute(tool, """{"command":"printf out; printf err >&2; exit 7"}""", cancellationToken);
        var missing = await Execute(tool, "{}", cancellationToken);
        var malformed = await Execute(tool, "[]", cancellationToken);
        var invalidEnvironment = await Execute(tool, """{"command":"true","env":{"VALUE":1}}""", cancellationToken);
        var malformedEnvironment = await Execute(tool, """{"command":"true","env":[]}""", cancellationToken);
        var environment = await Execute(tool, """{"command":"printf '%s' \"$COMMAND_VALUE\"","env":{"COMMAND_VALUE":"available"}}""", cancellationToken);
        var emptyEnvironment = await Execute(tool, """{"command":"printf '<%s>' \"$COMMAND_VALUE\"","env":{"COMMAND_VALUE":""}}""", cancellationToken);
        var inheritedPath = await Execute(tool, """{"command":"printf '%s' \"$PATH\""}""", cancellationToken);
        var emptyEnvironmentName = await Execute(tool, """{"command":"true","env":{"":"value"}}""", cancellationToken);
        var invalidEnvironmentName = await Execute(tool, """{"command":"true","env":{"INVALID=NAME":"value"}}""", cancellationToken);
        var invalidEnvironmentValue = await Execute(tool, """{"command":"true","env":{"VALUE":"\u0000"}}""", cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo("Process exited with code 7\n[stdout]\nout\n[stderr]\nerr");
        _ = await Assert.That(missing.Text).IsEqualTo("error: Tool arguments require a string 'command'.");
        _ = await Assert.That(malformed.Text).IsEqualTo("error: Tool arguments require a string 'command'.");
        _ = await Assert.That(invalidEnvironment.Text)
            .IsEqualTo("error: Tool argument 'env' must contain only string values.");
        _ = await Assert.That(malformedEnvironment.Text)
            .IsEqualTo("error: Tool argument 'env' must be an object containing string values.");
        _ = await Assert.That(environment.Text).IsEqualTo("Process exited with code 0\n[stdout]\navailable");
        _ = await Assert.That(emptyEnvironment.Text).IsEqualTo("Process exited with code 0\n[stdout]\n<>");
        _ = await Assert.That(inheritedPath.Text)
            .IsEqualTo($"Process exited with code 0\n[stdout]\n{Environment.GetEnvironmentVariable("PATH")}");
        _ = await Assert.That(emptyEnvironmentName.Text)
            .IsEqualTo("error: Tool argument 'env' contains an invalid environment value.");
        _ = await Assert.That(invalidEnvironmentName.Text)
            .IsEqualTo("error: Tool argument 'env' contains an invalid environment value.");
        _ = await Assert.That(invalidEnvironmentValue.Text)
            .IsEqualTo("error: Tool argument 'env' contains an invalid environment value.");

        var spilled = await Execute(tool, """{"command":"awk 'BEGIN { for (i = 0; i < 70000; i++) printf \"x\" }'"}""", cancellationToken);
        const string spilledPrefix =
            "Process exited with code 0\nTool output exceeded 64 KiB and was saved to ";
        const string spilledSuffix = ".";
        var spilledPath = spilled.Text[spilledPrefix.Length..^spilledSuffix.Length];
        _ = await Assert.That(spilled.Text).IsEqualTo(spilledPrefix + spilledPath + spilledSuffix);
        _ = await Assert.That(Path.IsPathFullyQualified(spilledPath)).IsTrue();
        _ = await Assert.That(Path.GetDirectoryName(spilledPath)).IsEqualTo(resources.BlobDirectory);

        var yielded = await Execute(tool, """{"command":"sleep 0.05; printf '%s' \"$LATER_VALUE\"","env":{"LATER_VALUE":"later"},"name":"later","yield_after_ms":0}""", cancellationToken);
        var waited = await Execute(new WaitProcessTool(processes), """{"name":"later"}""", cancellationToken);
        var reusedAfterCompletion = await Execute(tool, """{"command":"printf reused","name":"later"}""", cancellationToken);
        var unknown = await Execute(new WaitProcessTool(processes), """{"name":"missing"}""", cancellationToken);
        var running = await Execute(tool, """{"command":"sleep 30","name":"running","yield_after_ms":0}""", cancellationToken);
        var runningDuplicate = await Execute(tool, """{"command":"true","name":"running"}""", cancellationToken);
        var interrupted = await Execute(new InterruptProcessTool(processes), """{"name":"running"}""", cancellationToken);
        var reusedAfterInterrupt = await Execute(tool, """{"command":"printf restarted","name":"running"}""", cancellationToken);

        _ = await Assert.That(yielded.Text).IsEqualTo("later");
        _ = await Assert.That(waited.Text).IsEqualTo("Process exited with code 0\n[stdout]\nlater");
        _ = await Assert.That(reusedAfterCompletion.Text).IsEqualTo("Process exited with code 0\n[stdout]\nreused");
        _ = await Assert.That(unknown.Text).IsEqualTo("error: Unknown shell process 'missing'.");
        _ = await Assert.That(running.Text).IsEqualTo("running");
        _ = await Assert.That(runningDuplicate.Text)
            .IsEqualTo("error: Shell process name 'running' is already reserved.");
        _ = await Assert.That(interrupted.Text).IsEqualTo("Shell process 'running' interrupted.");
        _ = await Assert.That(reusedAfterInterrupt.Text).IsEqualTo("Process exited with code 0\n[stdout]\nrestarted");
    }

    private static Task<ToolExecutionResult> Execute(
        ExecCommandTool tool,
        string argumentsJson,
        CancellationToken cancellationToken) =>
        tool.Execute(new ToolInvocation("call-id", argumentsJson), cancellationToken);

    private static Task<ToolExecutionResult> Execute(
        WaitProcessTool tool,
        string argumentsJson,
        CancellationToken cancellationToken) =>
        tool.Execute(new ToolInvocation("call-id", argumentsJson), cancellationToken);

    private static Task<ToolExecutionResult> Execute(
        InterruptProcessTool tool,
        string argumentsJson,
        CancellationToken cancellationToken) =>
        tool.Execute(new ToolInvocation("call-id", argumentsJson), cancellationToken);

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
