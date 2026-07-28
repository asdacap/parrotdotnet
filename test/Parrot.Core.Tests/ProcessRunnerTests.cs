using Parrot.Process;
using Parrot.Security;

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
                await runner.Run(
                    $"touch {marker}",
                    _workspace,
                    Path.Combine(_workspace, "blob"),
                    WritableProfile(),
                    CancellationToken.None))
            .Throws<SandboxUnavailableException>();

        _ = await Assert.That(File.Exists(marker)).IsFalse();
    }

    [Test]
    public async Task Oversized_output_is_spilled_completely(
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
            Path.Combine(_workspace, "blob"),
            WritableProfile(),
            cancellationToken);

        _ = await Assert.That(result.ExitCode).IsEqualTo(7);
        _ = await Assert.That(result.Stdout).IsEmpty();
        _ = await Assert.That(result.Stderr).IsEmpty();
        _ = await Assert.That(result.Spilled).IsTrue();
        _ = await Assert.That(Path.IsPathFullyQualified(result.BlobPath)).IsTrue();
        _ = await Assert.That(Path.GetDirectoryName(result.BlobPath))
            .IsEqualTo(Path.Combine(_workspace, "blob"));
        _ = await Assert.That(Path.GetFileName(result.BlobPath)).EndsWith("-arse.dat");

        var output = await File.ReadAllTextAsync(result.BlobPath, cancellationToken);
        _ = await Assert.That(output).IsEqualTo(
            $"Process exited with code 7\n[stdout]\n{new string('o', 70000)}"
            + $"\n[stderr]\n{new string('e', 70000)}");
        _ = await Assert.That(Directory.EnumerateFiles(Path.Combine(_workspace, "blob"), ".process-*.tmp"))
            .IsEmpty();
    }

    [Test]
    public async Task Spill_failure_stops_a_producer_instead_of_deadlocking(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var runner = new ProcessRunner(CreateSandboxPassThrough(_workspace));
        var notDirectory = Path.Combine(_workspace, "not-a-directory");
        await File.WriteAllTextAsync(notDirectory, string.Empty, cancellationToken);

        _ = await Assert.That(async () =>
                await runner.Run(
                    "awk 'BEGIN { for (i = 0; i < 1000000; i++) printf \"x\" }'",
                    _workspace,
                    notDirectory,
                    WritableProfile(),
                    cancellationToken))
            .Throws<IOException>();
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
            Path.Combine(_workspace, "blob"),
            WritableProfile(),
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
    public async Task Linked_worktree_makes_repository_root_writable(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var repository = Path.Combine(_workspace, "repository");
        var worktree = Path.Combine(_workspace, "worktree");
        var gitDirectory = Path.Combine(repository, ".git", "worktrees", "linked");
        _ = Directory.CreateDirectory(gitDirectory);
        _ = Directory.CreateDirectory(worktree);
        await File.WriteAllTextAsync(
            Path.Combine(worktree, ".git"),
            $"gitdir: {gitDirectory}\n",
            cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(gitDirectory, "commondir"), "../..\n", cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(gitDirectory, "gitdir"),
            Path.Combine(worktree, ".git") + "\n",
            cancellationToken);
        var argumentsPath = Path.Combine(worktree, "arguments");
        var runner = new ProcessRunner(CreateArgumentCapturingSandbox(worktree, argumentsPath));

        _ = await runner.Run(
            "true",
            worktree,
            Path.Combine(worktree, "blob"),
            WritableProfile(),
            cancellationToken);

        var arguments = await File.ReadAllLinesAsync(argumentsPath, cancellationToken);
        var repositoryBind = Array.FindIndex(
            arguments,
            argument => string.Equals(argument, repository, StringComparison.Ordinal));
        _ = await Assert.That(repositoryBind).IsGreaterThan(0);
        _ = await Assert.That(arguments[repositoryBind - 1]).IsEqualTo("--bind");
        _ = await Assert.That(arguments[repositoryBind + 1]).IsEqualTo(repository);

        _ = await runner.Run(
            "true",
            worktree,
            Path.Combine(worktree, "blob"),
            SecurityProfile.Compose(true, [], [], []),
            cancellationToken);

        arguments = await File.ReadAllLinesAsync(argumentsPath, cancellationToken);
        _ = await Assert.That(FindMounts(arguments, repository)).IsEmpty();
        _ = await Assert.That(FindMounts(arguments, worktree)).IsEmpty();
        _ = await Assert.That(FindMounts(
            arguments,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"))).IsEmpty();
    }

    [Test]
    public async Task User_cache_directory_is_writable(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var argumentsPath = Path.Combine(_workspace, "arguments");
        var runner = new ProcessRunner(CreateArgumentCapturingSandbox(_workspace, argumentsPath));

        _ = await runner.Run(
            "true",
            _workspace,
            Path.Combine(_workspace, "blob"),
            WritableProfile(),
            cancellationToken);

        var arguments = await File.ReadAllLinesAsync(argumentsPath, cancellationToken);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        await AssertWritableBind(arguments, Path.Combine(home, ".cache"));
    }

    [Test]
    public async Task Security_profile_controls_baseline_and_applies_rules_in_order(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var nested = Directory.CreateDirectory(Path.Combine(_workspace, "nested")).FullName;
        var hidden = Directory.CreateDirectory(Path.Combine(_workspace, "hidden")).FullName;
        var argumentsPath = Path.Combine(_workspace, "arguments");
        var runner = new ProcessRunner(CreateArgumentCapturingSandbox(_workspace, argumentsPath));
        var profile = SecurityProfile.Compose(
            readOnly: true,
            modeRules:
            [
                new SandboxRule(_workspace, SandboxRuleAction.AllowWrite),
                new SandboxRule(_workspace, SandboxRuleAction.DenyWrite),
                new SandboxRule(nested, SandboxRuleAction.AllowWrite),
                new SandboxRule(hidden, SandboxRuleAction.DenyRead),
                new SandboxRule(hidden, SandboxRuleAction.AllowRead),
            ],
            globalRules: [],
            runtimeCapabilities: []);

        _ = await runner.Run(
            "true",
            _workspace,
            Path.Combine(_workspace, "blob"),
            profile,
            cancellationToken);

        var arguments = await File.ReadAllLinesAsync(argumentsPath, cancellationToken);
        var workspaceRules = FindMounts(arguments, _workspace);
        var nestedRules = FindMounts(arguments, nested);
        var hiddenRules = FindMounts(arguments, hidden);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        _ = await Assert.That(string.Join(',', workspaceRules)).IsEqualTo("--bind,--ro-bind");
        _ = await Assert.That(string.Join(',', nestedRules)).IsEqualTo("--bind");
        _ = await Assert.That(string.Join(',', hiddenRules)).IsEqualTo("--tmpfs,--ro-bind");
        _ = await Assert.That(Array.IndexOf(arguments, _workspace))
            .IsLessThan(Array.LastIndexOf(arguments, _workspace));
        _ = await Assert.That(FindSources(arguments, nested)).Contains("--bind");
        _ = await Assert.That(FindSources(arguments, hidden)).Contains("--ro-bind");
        _ = await Assert.That(arguments).DoesNotContain(Path.Combine(home, ".cache"));
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
            Path.Combine(_workspace, "blob"),
            WritableProfile(),
            cancellationToken);

        _ = await Assert.That(File.Exists(Path.Combine(_workspace, "inside.txt"))).IsTrue();
        _ = await Assert.That(result.Stdout).Contains("blocked");
        _ = await Assert.That(File.Exists("/host-write")).IsFalse();
    }

    private static SecurityProfile WritableProfile() => SecurityProfile.Compose(false, [], [], []);

    private static string[] FindMounts(string[] arguments, string path)
    {
        var mounts = new List<string>();

        for (var index = 0; index < arguments.Length - 1; index++)
        {
            if (arguments[index] == "--tmpfs"
                && string.Equals(arguments[index + 1], path, StringComparison.Ordinal))
            {
                mounts.Add(arguments[index]);
            }
            else if (index < arguments.Length - 2
                && (arguments[index] == "--bind" || arguments[index] == "--ro-bind")
                && string.Equals(arguments[index + 2], path, StringComparison.Ordinal))
            {
                mounts.Add(arguments[index]);
            }
        }

        return [.. mounts];
    }

    private static string[] FindSources(string[] arguments, string path)
    {
        var mounts = new List<string>();

        for (var index = 0; index < arguments.Length - 2; index++)
        {
            if ((arguments[index] == "--bind" || arguments[index] == "--ro-bind")
                && string.Equals(arguments[index + 1], path, StringComparison.Ordinal))
            {
                mounts.Add(arguments[index]);
            }
        }

        return [.. mounts];
    }

    private static async Task AssertWritableBind(string[] arguments, string directory)
    {
        var bind = Array.FindIndex(arguments, argument => string.Equals(argument, directory, StringComparison.Ordinal));
        _ = await Assert.That(bind).IsGreaterThan(0);
        _ = await Assert.That(arguments[bind - 1]).IsEqualTo("--bind");
        _ = await Assert.That(arguments[bind + 1]).IsEqualTo(directory);
    }

    private static string CreateArgumentCapturingSandbox(string workspace, string argumentsPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        var path = Path.Combine(workspace, "capturing-sandbox");
        var script = $"#!/bin/sh\nprintf '%s\\n' \"$@\" > '{argumentsPath}'\n"
            + "while [ \"$1\" != \"--\" ]; do shift; done\nshift\nexec \"$@\"\n";
        File.WriteAllText(path, script);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
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
