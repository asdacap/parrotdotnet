using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Security;
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
            null,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            null,
            null,
            null,
            CancellationToken.None);
        var processes = new ShellProcessOwner(
            _workspace,
            Path.Combine(_workspace, "blob"),
            new ProcessRunner(CreateSandboxPassThrough(_workspace)),
            CancellationToken.None);
        var tool = new ExecCommandTool(
            processes,
            session,
            SecurityProfile.Compose(readOnly: false, [], [], []));

        var result = await tool.Execute(
            """{"command":"printf out; printf err >&2; exit 7"}""", cancellationToken);
        var missing = await tool.Execute("{}", cancellationToken);
        var malformed = await tool.Execute("[]", cancellationToken);

        _ = await Assert.That(result).IsEqualTo("Process exited with code 7\n[stdout]\nout\n[stderr]\nerr");
        _ = await Assert.That(missing).IsEqualTo("error: Tool arguments require a string 'command'.");
        _ = await Assert.That(malformed).IsEqualTo("error: Tool arguments require a string 'command'.");

        var spilled = await tool.Execute(
            """{"command":"awk 'BEGIN { for (i = 0; i < 70000; i++) printf \"x\" }'"}""",
            cancellationToken);
        const string spilledPrefix =
            "Process exited with code 0\nTool output exceeded 64 KiB and was saved to ";
        const string spilledSuffix = ". Use exec_command to read the file.";
        var spilledPath = spilled[spilledPrefix.Length..^spilledSuffix.Length];
        _ = await Assert.That(spilled).IsEqualTo(spilledPrefix + spilledPath + spilledSuffix);
        _ = await Assert.That(Path.IsPathFullyQualified(spilledPath)).IsTrue();
        _ = await Assert.That(Path.GetDirectoryName(spilledPath)).IsEqualTo(Path.Combine(_workspace, "blob"));

        var yielded = await tool.Execute(
            """{"command":"sleep 0.05; printf later","name":"later","yield_after_ms":0}""",
            cancellationToken);
        var waited = await new WaitProcessTool(processes).Execute(
            """{"name":"later"}""", cancellationToken);
        var duplicate = await tool.Execute(
            """{"command":"true","name":"later"}""", cancellationToken);
        var unknown = await new WaitProcessTool(processes).Execute(
            """{"name":"missing"}""", cancellationToken);
        var running = await tool.Execute(
            """{"command":"sleep 30","name":"running","yield_after_ms":0}""",
            cancellationToken);
        var interrupted = await new InterruptProcessTool(processes).Execute(
            """{"name":"running"}""", cancellationToken);

        _ = await Assert.That(yielded).IsEqualTo("later");
        _ = await Assert.That(waited).IsEqualTo("Process exited with code 0\n[stdout]\nlater");
        _ = await Assert.That(duplicate).IsEqualTo("error: Shell process name 'later' is already reserved.");
        _ = await Assert.That(unknown).IsEqualTo("error: Unknown shell process 'missing'.");
        _ = await Assert.That(running).IsEqualTo("running");
        _ = await Assert.That(interrupted).IsEqualTo("Shell process 'running' interrupted.");
    }

    private static string CreateSandboxPassThrough(string workspace)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        var path = Path.Combine(workspace, "sandbox");
        var script = "#!/bin/sh\nwhile [ \"$1\" != \"--\" ]; do\n"
            + "  if [ \"$1\" = \"--chdir\" ]; then shift; cd \"$1\" || exit; fi\n"
            + "  shift\ndone\nshift\nexec \"$@\"\n";
        File.WriteAllText(path, script);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
