using Parrot.Process;
using Parrot.Security;
using Parrot.State;
using Parrot.Store;

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
                    ProcessEnvironmentOverrides.Empty,
                    Resources(_workspace),
                    Scratch(_workspace),
                    WritableProfile(Resources(_workspace)),
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
            ProcessEnvironmentOverrides.Empty,
            Resources(_workspace),
            Scratch(_workspace),
            WritableProfile(Resources(_workspace)),
            cancellationToken);

        _ = await Assert.That(result.ExitCode).IsEqualTo(7);
        _ = await Assert.That(result.Stdout).IsEmpty();
        _ = await Assert.That(result.Stderr).IsEmpty();
        _ = await Assert.That(result.Spilled).IsTrue();
        _ = await Assert.That(Path.IsPathFullyQualified(result.BlobPath)).IsTrue();
        _ = await Assert.That(Path.GetDirectoryName(result.BlobPath))
            .IsEqualTo(Scratch(_workspace).BlobDirectory);
        _ = await Assert.That(Path.GetFileName(result.BlobPath)).EndsWith("-arse.dat");

        var output = await File.ReadAllTextAsync(result.BlobPath, cancellationToken);
        _ = await Assert.That(output).IsEqualTo(
            $"Process exited with code 7\n[stdout]\n{new string('o', 70000)}"
            + $"\n[stderr]\n{new string('e', 70000)}");
        _ = await Assert.That(Directory.EnumerateFiles(Scratch(_workspace).BlobDirectory, ".process-*.tmp"))
            .IsEmpty();
    }

    [Test]
    public async Task Combined_formatted_output_over_65536_utf8_bytes_is_spilled(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var runner = new ProcessRunner(CreateSandboxPassThrough(_workspace));

        var result = await runner.Run(
            "awk 'BEGIN { for (i = 0; i < 11000; i++) printf \"€\"; "
            + "for (i = 0; i < 11000; i++) printf \"€\" > \"/dev/stderr\" }'",
            ProcessEnvironmentOverrides.Empty,
            Resources(_workspace),
            Scratch(_workspace),
            WritableProfile(Resources(_workspace)),
            cancellationToken);

        _ = await Assert.That(result.Spilled).IsTrue();
        var output = await File.ReadAllTextAsync(result.BlobPath, cancellationToken);
        _ = await Assert.That(output).IsEqualTo(
            $"Process exited with code 0\n[stdout]\n{new string('€', 11000)}"
            + $"\n[stderr]\n{new string('€', 11000)}");
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
        var resources = Resources(_workspace);
        var scratch = Scratch(resources);
        Directory.Delete(scratch.BlobDirectory);
        await File.WriteAllTextAsync(scratch.BlobDirectory, string.Empty, cancellationToken);

        _ = await Assert.That(async () =>
                await runner.Run(
                    "awk 'BEGIN { for (i = 0; i < 1000000; i++) printf \"x\" }'",
                    ProcessEnvironmentOverrides.Empty,
                    resources,
                    scratch,
                    WritableProfile(resources),
                    cancellationToken))
            .Throws<InvalidOperationException>();
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
            ProcessEnvironmentOverrides.Empty,
            Resources(_workspace),
            Scratch(_workspace),
            WritableProfile(Resources(_workspace)),
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
            ProcessEnvironmentOverrides.Empty,
            Resources(worktree),
            Scratch(worktree),
            WritableProfile(Resources(worktree)),
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
            ProcessEnvironmentOverrides.Empty,
            Resources(worktree),
            Scratch(worktree),
            AgentProfile(Resources(worktree), SecurityProfile.Compose(true, [], [], []), []),
            cancellationToken);

        arguments = await File.ReadAllLinesAsync(argumentsPath, cancellationToken);
        _ = await Assert.That(FindMounts(arguments, repository)).IsEmpty();
        _ = await Assert.That(FindMounts(arguments, worktree)).IsEmpty();
        _ = await Assert.That(FindMounts(
            arguments,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"))).IsEmpty();
    }

    [Test]
    public async Task Command_environment_overrides_are_forwarded_to_the_sandbox(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var argumentsPath = Path.Combine(_workspace, "arguments");
        var runner = new ProcessRunner(CreateArgumentCapturingSandbox(_workspace, argumentsPath));

        _ = await runner.Run(
            "true",
            new ProcessEnvironmentOverrides(
            [
                new KeyValuePair<string, string>("LANG", "command-language"),
                new KeyValuePair<string, string>("COMMAND_VALUE", "present"),
            ]),
            Resources(_workspace),
            Scratch(_workspace),
            WritableProfile(Resources(_workspace)),
            cancellationToken);

        var arguments = await File.ReadAllLinesAsync(argumentsPath, cancellationToken);
        _ = await Assert.That(arguments).DoesNotContain("--clearenv");
        _ = await Assert.That(FindSetEnvironment(arguments, "COMMAND_VALUE")).IsEqualTo("present");
        _ = await Assert.That(FindSetEnvironment(arguments, "LANG")).IsEqualTo("command-language");
        _ = await Assert.That(LastSetEnvironmentIndex(arguments, "COMMAND_VALUE"))
            .IsLessThan(Array.IndexOf(arguments, "--chdir"));
        _ = await Assert.That(LastSetEnvironmentIndex(arguments, "HOME")).IsEqualTo(-1);
        _ = await Assert.That(LastSetEnvironmentIndex(arguments, "XDG_CACHE_HOME")).IsEqualTo(-1);
        _ = await Assert.That(LastSetEnvironmentIndex(arguments, "TMPDIR")).IsEqualTo(-1);
    }

    [Test]
    public async Task Agent_scratch_directory_is_writable(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var argumentsPath = Path.Combine(_workspace, "arguments");
        var runner = new ProcessRunner(CreateArgumentCapturingSandbox(_workspace, argumentsPath));

        var resources = Resources(_workspace);
        var scratch = Scratch(resources);
        _ = await runner.Run(
            "true",
            ProcessEnvironmentOverrides.Empty,
            resources,
            Scratch(resources),
            WritableProfile(resources),
            cancellationToken);

        var arguments = await File.ReadAllLinesAsync(argumentsPath, cancellationToken);
        await AssertWritableBind(arguments, scratch.Root);
        _ = await Assert.That(LastSetEnvironmentIndex(arguments, "HOME")).IsEqualTo(-1);
        _ = await Assert.That(LastSetEnvironmentIndex(arguments, "XDG_CACHE_HOME")).IsEqualTo(-1);
        _ = await Assert.That(LastSetEnvironmentIndex(arguments, "TMPDIR")).IsEqualTo(-1);
    }

    [Test]
    public async Task Agent_scratch_directory_overrides_profile_restrictions(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var argumentsPath = Path.Combine(_workspace, "arguments");
        var runner = new ProcessRunner(CreateArgumentCapturingSandbox(_workspace, argumentsPath));
        var resources = Resources(_workspace);
        var scratch = Scratch(resources);
        var profile = SecurityProfile.Compose(
            false,
            [new SandboxRule(scratch.Root, SandboxRuleAction.DenyWrite)],
            [],
            []);

        _ = await runner.Run(
            "true",
            ProcessEnvironmentOverrides.Empty,
            resources,
            Scratch(resources),
            AgentProfile(resources, profile, []),
            cancellationToken);

        var arguments = await File.ReadAllLinesAsync(argumentsPath, cancellationToken);
        var mounts = FindMounts(arguments, scratch.Root);
        _ = await Assert.That(mounts[^1]).IsEqualTo("--bind");
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
            mandatoryRules: []);

        _ = await runner.Run(
            "true",
            ProcessEnvironmentOverrides.Empty,
            Resources(_workspace),
            Scratch(_workspace),
            AgentProfile(Resources(_workspace), profile, []),
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
    public async Task Approvals_are_ordered_below_static_policy(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var granted = Directory.CreateDirectory(Path.Combine(_workspace, "granted")).FullName;
        var argumentsPath = Path.Combine(_workspace, "arguments");
        var runner = new ProcessRunner(CreateArgumentCapturingSandbox(_workspace, argumentsPath));
        var approval = SecurityWriteTarget.Resolve(granted);
        var profile = SecurityProfile.Compose(
            false,
            [new SandboxRule(granted, SandboxRuleAction.DenyWrite)],
            [],
            []);

        _ = await runner.Run(
            "true",
            ProcessEnvironmentOverrides.Empty,
            Resources(_workspace),
            Scratch(_workspace),
            AgentProfile(Resources(_workspace), profile, [approval]),
            cancellationToken);

        var arguments = await File.ReadAllLinesAsync(argumentsPath, cancellationToken);
        _ = await Assert.That(string.Join(',', FindMounts(arguments, granted)))
            .IsEqualTo("--bind,--ro-bind");

        _ = await runner.Run(
            "true",
            ProcessEnvironmentOverrides.Empty,
            Resources(_workspace),
            Scratch(_workspace),
            AgentProfile(Resources(_workspace), SecurityProfile.Compose(true, [], [], []), []),
            cancellationToken);

        arguments = await File.ReadAllLinesAsync(argumentsPath, cancellationToken);
        _ = await Assert.That(FindMounts(arguments, granted)).IsEmpty();
    }

    [Test]
    public async Task Mandatory_profile_rules_override_configuration_and_approvals(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var mandatoryRoot = Directory.CreateDirectory(Path.Combine(_workspace, "mandatory")).FullName;
        var argumentsPath = Path.Combine(_workspace, "arguments");
        var runner = new ProcessRunner(CreateArgumentCapturingSandbox(_workspace, argumentsPath));
        var approval = SecurityWriteTarget.Resolve(mandatoryRoot);
        var profile = SecurityProfile.Compose(
            readOnly: false,
            modeRules: [new SandboxRule(mandatoryRoot, SandboxRuleAction.AllowWrite)],
            globalRules: [],
            mandatoryRules: [new SandboxRule(mandatoryRoot, SandboxRuleAction.DenyRead)]);

        _ = await runner.Run(
            "true",
            ProcessEnvironmentOverrides.Empty,
            Resources(_workspace),
            Scratch(_workspace),
            AgentProfile(Resources(_workspace), profile, [approval]),
            cancellationToken);

        var arguments = await File.ReadAllLinesAsync(argumentsPath, cancellationToken);
        _ = await Assert.That(FindMounts(arguments, mandatoryRoot)[^1]).IsEqualTo("--tmpfs");
    }

    [Test]
    public async Task Real_sandbox_applies_directory_grants_and_static_precedence(
        CancellationToken cancellationToken)
    {
        var runner = ProcessRunner.Locate();
        if (!runner.SandboxAvailable)
        {
            return;
        }

        var external = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"parrot-runner-grants-{Guid.NewGuid():n}"));
        try
        {
            var granted = Directory.CreateDirectory(Path.Combine(external.FullName, "granted")).FullName;
            var allowed = Path.Combine(granted, "allowed.txt");
            var staticallyDenied = Path.Combine(granted, "denied.txt");
            var outsideGrant = Path.Combine(external.FullName, "outside.txt");
            await File.WriteAllTextAsync(allowed, "old", cancellationToken);
            await File.WriteAllTextAsync(staticallyDenied, "old", cancellationToken);
            await File.WriteAllTextAsync(outsideGrant, "old", cancellationToken);
            var approval = SecurityWriteTarget.Resolve(granted);
            var profile = SecurityProfile.Compose(
                false,
                [new SandboxRule(staticallyDenied, SandboxRuleAction.DenyWrite)],
                [],
                []);

            var result = await runner.Run(
                $"printf allowed > '{allowed}'; "
                + $"if printf denied > '{staticallyDenied}' 2>/dev/null; then echo static-writable; else echo static-denied; fi; "
                + $"printf outside > '{outsideGrant}' 2>/dev/null",
                ProcessEnvironmentOverrides.Empty,
                Resources(_workspace),
                Scratch(_workspace),
                AgentProfile(Resources(_workspace), profile, [approval]),
                cancellationToken);

            _ = await Assert.That(result.Stdout).Contains("static-denied");
            _ = await Assert.That(await File.ReadAllTextAsync(allowed, cancellationToken)).IsEqualTo("allowed");
            _ = await Assert.That(await File.ReadAllTextAsync(staticallyDenied, cancellationToken)).IsEqualTo("old");
            _ = await Assert.That(await File.ReadAllTextAsync(outsideGrant, cancellationToken)).IsEqualTo("old");
        }
        finally
        {
            external.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Real_sandbox_applies_exact_file_grants(CancellationToken cancellationToken)
    {
        var runner = ProcessRunner.Locate();
        if (!runner.SandboxAvailable)
        {
            return;
        }

        var external = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"parrot-runner-file-grants-{Guid.NewGuid():n}"));
        try
        {
            var allowed = Path.Combine(external.FullName, "allowed.txt");
            var denied = Path.Combine(external.FullName, "denied.txt");
            await File.WriteAllTextAsync(allowed, "old", cancellationToken);
            await File.WriteAllTextAsync(denied, "old", cancellationToken);
            var approval = SecurityWriteTarget.Resolve(allowed);

            _ = await runner.Run(
                $"printf allowed > '{allowed}'; printf denied > '{denied}' 2>/dev/null",
                ProcessEnvironmentOverrides.Empty,
                Resources(_workspace),
                Scratch(_workspace),
                AgentProfile(Resources(_workspace), SecurityProfile.Compose(false, [], [], []), [approval]),
                cancellationToken);

            _ = await Assert.That(await File.ReadAllTextAsync(allowed, cancellationToken)).IsEqualTo("allowed");
            _ = await Assert.That(await File.ReadAllTextAsync(denied, cancellationToken)).IsEqualTo("old");
        }
        finally
        {
            external.Delete(recursive: true);
        }
    }

    [Test]
    public async Task Replaced_approval_is_rejected_before_launch()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var granted = Directory.CreateDirectory(Path.Combine(_workspace, "granted")).FullName;
        var replacement = Directory.CreateDirectory(Path.Combine(_workspace, "replacement")).FullName;
        var approval = SecurityWriteTarget.Resolve(granted);
        Directory.Delete(granted);
        _ = Directory.CreateSymbolicLink(granted, replacement);

        _ = await Assert.That(() => AgentProfile(
                Resources(_workspace),
                SecurityProfile.Compose(false, [], [], []),
                [approval]))
            .Throws<InvalidOperationException>();
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

        var resources = Resources(_workspace);
        var scratch = Scratch(resources);
        var scratchFile = Path.Combine(scratch.Root, "scratch.txt");
        var result = await runner.Run(
            $"echo hi > inside.txt && echo scratch > '{scratchFile}' && (touch /host-write 2>&1 || echo blocked)",
            ProcessEnvironmentOverrides.Empty,
            resources,
            Scratch(resources),
            WritableProfile(resources),
            cancellationToken);

        _ = await Assert.That(File.Exists(Path.Combine(_workspace, "inside.txt"))).IsTrue();
        _ = await Assert.That(await File.ReadAllTextAsync(scratchFile, cancellationToken)).IsEqualTo("scratch\n");
        _ = await Assert.That(result.Stdout).Contains("blocked");
        _ = await Assert.That(File.Exists("/host-write")).IsFalse();
    }

    private static SecurityProfile WritableProfile(UserSessionResources resources) =>
        AgentProfile(resources, SecurityProfile.Compose(false, [], [], []), []);

    private static SecurityProfile AgentProfile(
        UserSessionResources resources,
        SecurityProfile policy,
        IEnumerable<SecurityWriteTarget> approvals) =>
        SecurityProfile.ForAgent(policy, resources.Workspace.WritableRoots, Scratch(resources).Root, approvals);

    private static UserSessionResources Resources(string workspace) =>
        new(
            new StatePaths(
                Path.Combine(workspace, ".test-state"),
                Path.Combine(workspace, ".test-config"),
                Path.Combine(workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(workspace));

    private static AgentScratchDirectory Scratch(string workspace) =>
        Resources(workspace).AgentScratch("agent-session-test");

    private static AgentScratchDirectory Scratch(UserSessionResources resources) =>
        resources.AgentScratch("agent-session-test");

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

    private static string FindSetEnvironment(string[] arguments, string name)
    {
        var index = LastSetEnvironmentIndex(arguments, name);
        return index < 0 ? string.Empty : arguments[index + 2];
    }

    private static int LastSetEnvironmentIndex(string[] arguments, string name)
    {
        for (var index = arguments.Length - 3; index >= 0; index--)
        {
            if (arguments[index] == "--setenv"
                && string.Equals(arguments[index + 1], name, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static async Task AssertWritableBind(string[] arguments, string directory)
    {
        var bind = -1;

        for (var index = 0; index < arguments.Length - 2; index++)
        {
            if (arguments[index] == "--bind"
                && string.Equals(arguments[index + 1], directory, StringComparison.Ordinal)
                && string.Equals(arguments[index + 2], directory, StringComparison.Ordinal))
            {
                bind = index;
                break;
            }
        }

        _ = await Assert.That(bind).IsGreaterThanOrEqualTo(0);
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
