using Parrot.Process;

namespace Parrot.Core.Tests;

internal sealed class ProcessRunnerTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "parrot-runner-tests", Guid.NewGuid().ToString("n"));

    public ProcessRunnerTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    // The security property: no sandbox, no execution. A runner with no
    // bubblewrap must refuse rather than run the command unconfined.
    [Test]
    public async Task Without_a_sandbox_the_command_does_not_run()
    {
        var runner = new ProcessRunner(string.Empty);
        var marker = Path.Combine(_workspace, "should-not-exist");

        _ = await Assert.That(async () =>
                await runner.Run($"touch {marker}", _workspace, CancellationToken.None))
            .Throws<SandboxUnavailableException>();

        _ = await Assert.That(File.Exists(marker)).IsFalse();
    }

    [Test]
    public async Task Output_is_bounded_and_reports_each_truncated_stream(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var runner = new ProcessRunner(CreateSandboxPassThrough(_workspace));

        var result = await runner.Run(
            "awk 'BEGIN { for (i = 0; i < 70000; i++) printf \"o\"; "
            + "for (i = 0; i < 70000; i++) printf \"e\" > \"/dev/stderr\"; exit 7 }'",
            _workspace,
            cancellationToken);

        _ = await Assert.That(result.ExitCode).IsEqualTo(7);
        _ = await Assert.That(result.Stdout.Length).IsEqualTo(64 << 10);
        _ = await Assert.That(result.Stderr.Length).IsEqualTo(64 << 10);
        _ = await Assert.That(result.StdoutTruncated).IsTrue();
        _ = await Assert.That(result.StderrTruncated).IsTrue();
    }

    [Test]
    public async Task Cancellation_kills_the_process_tree_before_returning()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var runner = new ProcessRunner(CreateSandboxPassThrough(_workspace));
        var pidPath = Path.Combine(_workspace, "child.pid");
        using var cancellation = new CancellationTokenSource();
        var running = runner.Run(
            "sh -c 'while :; do sleep 1; done' & echo $! > child.pid; wait",
            _workspace,
            cancellation.Token);
        var childPid = await ReadPid(pidPath);

        await cancellation.CancelAsync();

        var canceled = false;

        try
        {
            _ = await running;
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        _ = await Assert.That(canceled).IsTrue();
        _ = await Assert.That(await WaitUntilExited(childPid)).IsTrue();
    }

    [Test]
    public async Task The_workspace_is_writable_and_the_host_is_read_only(CancellationToken cancellationToken)
    {
        var runner = ProcessRunner.Locate();

        if (!runner.SandboxAvailable)
        {
            // bubblewrap is absent in this environment; the fail-closed test
            // above still holds, and CI runs this one where bwrap exists.
            return;
        }

        var result = await runner.Run(
            "echo hi > inside.txt && (touch /host-write 2>&1 || echo blocked)",
            _workspace,
            cancellationToken);

        _ = await Assert.That(File.Exists(Path.Combine(_workspace, "inside.txt"))).IsTrue();
        _ = await Assert.That(result.Stdout).Contains("blocked");
        _ = await Assert.That(File.Exists("/host-write")).IsFalse();
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

    private static async Task<int> ReadPid(string path)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (File.Exists(path)
                && int.TryParse(await File.ReadAllTextAsync(path), out var processId))
            {
                return processId;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5));
        }

        throw new InvalidOperationException("The child process did not write its process ID.");
    }

    private static async Task<bool> WaitUntilExited(int processId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (!Directory.Exists($"/proc/{processId}"))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5));
        }

        return false;
    }
}
