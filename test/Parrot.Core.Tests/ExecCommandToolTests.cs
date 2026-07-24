using Parrot.Process;
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

        var tool = new ExecCommandTool(
            _workspace,
            Path.Combine(_workspace, "blob"),
            new ProcessRunner(CreateSandboxPassThrough(_workspace)));

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
        _ = await Assert.That(Path.IsPathFullyQualified(spilled)).IsTrue();
        _ = await Assert.That(Path.GetDirectoryName(spilled)).IsEqualTo(Path.Combine(_workspace, "blob"));
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
