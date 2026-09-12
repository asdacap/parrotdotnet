using Parrot.Agent;
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
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        var runner = new ProcessRunner(string.Empty);
        var marker = Path.Combine(_workspace, "should-not-exist");

        _ = await Assert.That(async () =>
                await runner.Run(
                    $"touch {marker}",
                    ProcessEnvironmentOverrides.Empty,
                    resources,
                    Scratch(resources),
                    WritableProfile(resources),
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

        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        var runner = new ProcessRunner(CreateSandboxPassThrough(_workspace));

        var result = await runner.Run(
            "awk 'BEGIN { for (i = 0; i < 70000; i++) printf \"o\"; "
            + "for (i = 0; i < 70000; i++) printf \"e\" > \"/dev/stderr\"; exit 7 }'",
            ProcessEnvironmentOverrides.Empty,
            resources,
            Scratch(resources),
            WritableProfile(resources),
            cancellationToken);

        _ = await Assert.That(result.ExitCode).IsEqualTo(7);
        _ = await Assert.That(result.Stdout).IsEmpty();
        _ = await Assert.That(result.Stderr).IsEmpty();
        _ = await Assert.That(result.Spilled).IsTrue();
        _ = await Assert.That(Path.IsPathFullyQualified(result.BlobPath)).IsTrue();
        _ = await Assert.That(Path.GetDirectoryName(result.BlobPath))
            .IsEqualTo(Scratch(resources).BlobDirectory);
        _ = await Assert.That(Path.GetFileName(result.BlobPath)).EndsWith("-arse.dat");

        var output = await File.ReadAllTextAsync(result.BlobPath, cancellationToken);
        _ = await Assert.That(output).StartsWith("Process exited with code 7 after ");
        _ = await Assert.That(output).EndsWith(
            $"s\n[stdout]\n{new string('o', 70000)}"
            + $"\n[stderr]\n{new string('e', 70000)}");
        _ = await Assert.That(Directory.EnumerateFiles(Scratch(resources).BlobDirectory, ".process-*.tmp"))
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

        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        var runner = new ProcessRunner(CreateSandboxPassThrough(_workspace));

        var result = await runner.Run(
            "awk 'BEGIN { for (i = 0; i < 11000; i++) printf \"€\"; "
            + "for (i = 0; i < 11000; i++) printf \"€\" > \"/dev/stderr\" }'",
            ProcessEnvironmentOverrides.Empty,
            resources,
            Scratch(resources),
            WritableProfile(resources),
            cancellationToken);

        _ = await Assert.That(result.Spilled).IsTrue();
        var output = await File.ReadAllTextAsync(result.BlobPath, cancellationToken);
        _ = await Assert.That(output).StartsWith("Process exited with code 0 after ");
        _ = await Assert.That(output).EndsWith(
            $"s\n[stdout]\n{new string('€', 11000)}"
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
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
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

        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        var runner = new ProcessRunner(CreateSandboxPassThrough(_workspace));
        var pidPath = Path.Combine(_workspace, "child.pid");
        using var cancellation = new CancellationTokenSource();
        var running = runner.Run(
            "sh -c 'while :; do sleep 1; done' & echo $! > child.pid; wait",
            ProcessEnvironmentOverrides.Empty,
            resources,
            Scratch(resources),
            WritableProfile(resources),
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
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(worktree, ".test-state"),
                Path.Combine(worktree, ".test-config"),
                Path.Combine(worktree, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(worktree));
        var argumentsPath = Path.Combine(worktree, "arguments");
        var runner = new ProcessRunner(CreateArgumentCapturingSandbox(worktree, argumentsPath));

        _ = await runner.Run(
            "true",
            ProcessEnvironmentOverrides.Empty,
            resources,
            Scratch(resources),
            WritableProfile(resources),
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
            resources,
            Scratch(resources),
            AgentProfile(resources, SecurityProfile.Compose(true, [], [], []), []),
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

        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        var argumentsPath = Path.Combine(_workspace, "arguments");
        var runner = new ProcessRunner(CreateArgumentCapturingSandbox(_workspace, argumentsPath));

        _ = await runner.Run(
            "true",
            new ProcessEnvironmentOverrides(
            [
                new KeyValuePair<string, string>("LANG", "command-language"),
                new KeyValuePair<string, string>("COMMAND_VALUE", "present"),
            ]),
            resources,
            Scratch(resources),
            WritableProfile(resources),
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
    public async Task User_session_scratch_root_is_writable(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var argumentsPath = Path.Combine(_workspace, "arguments");
        var runner = new ProcessRunner(CreateArgumentCapturingSandbox(_workspace, argumentsPath));

        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        _ = await runner.Run(
            "true",
            ProcessEnvironmentOverrides.Empty,
            resources,
            Scratch(resources),
            WritableProfile(resources),
            cancellationToken);

        var arguments = await File.ReadAllLinesAsync(argumentsPath, cancellationToken);
        await AssertWritableBind(arguments, resources.ScratchRootDirectory);
        _ = await Assert.That(LastSetEnvironmentIndex(arguments, "HOME")).IsEqualTo(-1);
        _ = await Assert.That(LastSetEnvironmentIndex(arguments, "XDG_CACHE_HOME")).IsEqualTo(-1);
        _ = await Assert.That(LastSetEnvironmentIndex(arguments, "TMPDIR")).IsEqualTo(-1);
    }

    [Test]
    public async Task User_session_scratch_root_overrides_profile_restrictions(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var argumentsPath = Path.Combine(_workspace, "arguments");
        var runner = new ProcessRunner(CreateArgumentCapturingSandbox(_workspace, argumentsPath));
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
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
        _ = await Assert.That(FindMounts(arguments, scratch.Root)).IsEmpty();
        await AssertWritableBind(arguments, resources.ScratchRootDirectory);
    }

    [Test]
    public async Task Restricted_shared_write_grant_is_materialized_as_a_writable_bind(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var shared = Directory.CreateDirectory(Path.Combine(_workspace, "shared")).FullName;
        var argumentsPath = Path.Combine(_workspace, "arguments");
        var runner = new ProcessRunner(CreateArgumentCapturingSandbox(_workspace, argumentsPath));
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        var parent = SecurityProfile.Compose(
            readOnly: false,
            modeRules: [new SandboxRule(shared, SandboxRuleAction.AllowWrite)],
            globalRules: [],
            mandatoryRules: []);
        var child = SecurityProfile.Compose(
            readOnly: false,
            modeRules: [new SandboxRule(shared, SandboxRuleAction.AllowWrite)],
            globalRules: [],
            mandatoryRules: []);
        var profile = AgentProfile(resources, parent.RestrictWith(child), []);

        _ = await runner.Run(
            "true",
            ProcessEnvironmentOverrides.Empty,
            resources,
            Scratch(resources),
            profile,
            cancellationToken);

        var arguments = await File.ReadAllLinesAsync(argumentsPath, cancellationToken);
        _ = await Assert.That(string.Join(',', FindMounts(arguments, shared))).IsEqualTo("--bind");
    }

    [Test]
    public async Task Security_profile_controls_baseline_and_applies_rules_in_order(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
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
            resources,
            Scratch(resources),
            AgentProfile(resources, profile, []),
            cancellationToken);

        var arguments = await File.ReadAllLinesAsync(argumentsPath, cancellationToken);
        var workspaceRules = FindMounts(arguments, _workspace);
        var nestedRules = FindMounts(arguments, nested);
        var hiddenRules = FindMounts(arguments, hidden);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        _ = await Assert.That(string.Join(',', workspaceRules)).IsEqualTo("--ro-bind");
        _ = await Assert.That(string.Join(',', nestedRules)).IsEqualTo("--bind");
        _ = await Assert.That(string.Join(',', hiddenRules)).IsEqualTo("--ro-bind");
        _ = await Assert.That(Array.IndexOf(arguments, _workspace))
            .IsLessThan(Array.LastIndexOf(arguments, _workspace));
        _ = await Assert.That(FindSources(arguments, nested)).Contains("--bind");
        _ = await Assert.That(FindSources(arguments, hidden)).Contains("--ro-bind");
        _ = await Assert.That(arguments).DoesNotContain(Path.Combine(home, ".cache"));
    }

    [Test]
    public async Task Virtual_filesystems_are_mounted_after_security_rules(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var argumentsPath = Path.Combine(_workspace, "arguments");
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        var scratch = Scratch(resources);
        var runner = new ProcessRunner(CreateArgumentCapturingSandbox(_workspace, argumentsPath));

        _ = await runner.Run(
            "true",
            ProcessEnvironmentOverrides.Empty,
            resources,
            scratch,
            WritableProfile(resources),
            cancellationToken);

        var arguments = await File.ReadAllLinesAsync(argumentsPath, cancellationToken);
        var lastSecurityRule = Array.LastIndexOf(arguments, resources.ScratchRootDirectory);
        var deviceMount = Array.IndexOf(arguments, "--dev");
        var processMount = Array.IndexOf(arguments, "--proc");
        _ = await Assert.That(deviceMount).IsGreaterThan(lastSecurityRule);
        _ = await Assert.That(processMount).IsGreaterThan(lastSecurityRule);
        _ = await Assert.That(arguments[deviceMount + 1]).IsEqualTo("/dev");
        _ = await Assert.That(arguments[processMount + 1]).IsEqualTo("/proc");
    }

    [Test]
    public async Task Approvals_are_ordered_below_static_policy(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
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
            resources,
            Scratch(resources),
            AgentProfile(resources, profile, [approval]),
            cancellationToken);

        var arguments = await File.ReadAllLinesAsync(argumentsPath, cancellationToken);
        _ = await Assert.That(string.Join(',', FindMounts(arguments, granted)))
            .IsEqualTo("--ro-bind");

        _ = await runner.Run(
            "true",
            ProcessEnvironmentOverrides.Empty,
            resources,
            Scratch(resources),
            AgentProfile(resources, SecurityProfile.Compose(true, [], [], []), []),
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

        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
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
            resources,
            Scratch(resources),
            AgentProfile(resources, profile, [approval]),
            cancellationToken);

        var arguments = await File.ReadAllLinesAsync(argumentsPath, cancellationToken);
        _ = await Assert.That(FindMounts(arguments, mandatoryRoot)[^1]).IsEqualTo("--tmpfs");
    }

    [Test]
    [Arguments("WORKDIR")]
    [Arguments("SCRATCH_DIR")]
    [Arguments("AGENT_SCRATCH_DIR")]
    [Arguments("AGENT_HISTORY_DIR")]
    public async Task Real_sandbox_path_environment_obeys_literal_path_permissions(
        string variableName,
        CancellationToken cancellationToken)
    {
        var runner = ProcessRunner.Locate();
        if (!runner.SandboxAvailable)
        {
            return;
        }

        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        var scratch = Scratch(resources);
        var paths = new AgentPathEnvironment(resources, scratch);
        var defaultDirectory = variableName switch
        {
            "WORKDIR" => resources.Workspace.LaunchDirectory,
            "SCRATCH_DIR" => resources.ScratchRootDirectory,
            "AGENT_SCRATCH_DIR" => scratch.Root,
            "AGENT_HISTORY_DIR" => Path.GetDirectoryName(scratch.HistoryPath)
                ?? throw new InvalidOperationException("The history path has no directory."),
            _ => throw new ArgumentOutOfRangeException(nameof(variableName)),
        };
        var deniedDirectory = Directory.CreateDirectory(
            Path.Combine(_workspace, "denied fixture $literal; value")).FullName;
        var profile = AgentProfile(
            resources,
            SecurityProfile.Compose(
                false,
                [new SandboxRule(deniedDirectory, SandboxRuleAction.DenyWrite)],
                [],
                []),
            []);

        foreach (var allowed in new[] { true, false })
        {
            var directory = allowed ? defaultDirectory : deniedDirectory;
            var environment = paths.Merge(allowed
                ? ProcessEnvironmentOverrides.Empty
                : new ProcessEnvironmentOverrides([new KeyValuePair<string, string>(variableName, directory)]));
            var variableFile = Path.Combine(directory, "variable.txt");
            var literalFile = Path.Combine(directory, "literal.txt");
            await File.WriteAllTextAsync(variableFile, "old", cancellationToken);
            await File.WriteAllTextAsync(literalFile, "old", cancellationToken);

            var result = await runner.Run(
                $"printf '%s\\n' \"${{{variableName}}}\"; "
                + $"if (printf updated > \"${{{variableName}}}/variable.txt\") 2>/dev/null; "
                + "then echo variable-allowed; else echo variable-denied; fi; "
                + $"if (printf updated > '{literalFile}') 2>/dev/null; "
                + "then echo literal-allowed; else echo literal-denied; fi",
                environment,
                resources,
                scratch,
                profile,
                cancellationToken);

            var outcome = allowed ? "allowed" : "denied";
            _ = await Assert.That(result.ExitCode).IsEqualTo(0);
            _ = await Assert.That(result.Stdout)
                .IsEqualTo($"{directory}\nvariable-{outcome}\nliteral-{outcome}\n");
            _ = await Assert.That(await File.ReadAllTextAsync(variableFile, cancellationToken))
                .IsEqualTo(allowed ? "updated" : "old");
            _ = await Assert.That(await File.ReadAllTextAsync(literalFile, cancellationToken))
                .IsEqualTo(allowed ? "updated" : "old");
        }
    }

    [Test]
    public async Task Real_sandbox_applies_directory_grants_and_static_precedence(
        CancellationToken cancellationToken)
    {
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
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
                resources,
                Scratch(resources),
                AgentProfile(resources, profile, [approval]),
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
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
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
                resources,
                Scratch(resources),
                AgentProfile(resources, SecurityProfile.Compose(false, [], [], []), [approval]),
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

        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        var granted = Directory.CreateDirectory(Path.Combine(_workspace, "granted")).FullName;
        var replacement = Directory.CreateDirectory(Path.Combine(_workspace, "replacement")).FullName;
        var approval = SecurityWriteTarget.Resolve(granted);
        Directory.Delete(granted);
        _ = Directory.CreateSymbolicLink(granted, replacement);

        _ = await Assert.That(() => AgentProfile(
                resources,
                SecurityProfile.Compose(false, [], [], []),
                [approval]))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Read_only_process_writes_sibling_scratch_but_not_session_state(
        CancellationToken cancellationToken)
    {
        var runner = ProcessRunner.Locate();

        if (!runner.SandboxAvailable)
        {
            return;
        }

        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        var ownScratch = Scratch(resources);
        var siblingScratch = resources.AgentScratch("agent-session-sibling");
        var siblingFile = Path.Combine(siblingScratch.Root, "shared.txt");
        var outsideFile = Path.Combine(resources.Root, "outside.txt");
        var policy = SecurityProfile.Compose(
            true,
            [new SandboxRule(siblingScratch.Root, SandboxRuleAction.DenyWrite)],
            [],
            []);
        var result = await runner.Run(
            $"printf shared > '{siblingFile}' && "
            + $"(printf denied > '{outsideFile}' 2>/dev/null || printf outside-blocked)",
            ProcessEnvironmentOverrides.Empty,
            resources,
            ownScratch,
            AgentProfile(resources, policy, []),
            cancellationToken);

        _ = await Assert.That(await File.ReadAllTextAsync(siblingFile, cancellationToken)).IsEqualTo("shared");
        _ = await Assert.That(result.Stdout).Contains("outside-blocked");
        _ = await Assert.That(File.Exists(outsideFile)).IsFalse();
    }

    [Test]
    public async Task Pipe_command_survives_the_calling_thread_exiting(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var runner = ProcessRunner.Locate();
        if (!runner.SandboxAvailable)
        {
            return;
        }

        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        var scratch = Scratch(resources);
        var securityProfile = WritableProfile(resources);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var releaseCaller = new ManualResetEventSlim();
        var started = new TaskCompletionSource<IProcessExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            try
            {
                started.SetResult(runner.Start(
                    "printf READY; while [ ! -f finish ]; do sleep 0.01; done; printf FINISHED; exit 7",
                    ProcessEnvironmentOverrides.Empty,
                    resources,
                    scratch,
                    securityProfile,
                    ShellProcessTerminalMode.Pipe,
                    timeout.Token));
                releaseCaller.Wait();
            }
            catch (Exception exception)
            {
                _ = started.TrySetException(exception);
            }
        })
        { IsBackground = true };
        caller.Start();

        try
        {
            await using var execution = await started.Task;
            var stdoutPath = execution.StdoutPath
                ?? throw new InvalidOperationException("Pipe output path is missing.");
            while (!string.Equals(
                       await File.ReadAllTextAsync(stdoutPath, timeout.Token),
                       "READY",
                       StringComparison.Ordinal))
            {
                if (execution.Result.IsCompleted)
                {
                    throw new InvalidOperationException($"Command exited before READY: {await execution.Result.WaitAsync(timeout.Token)}");
                }

                await Task.Delay(10, timeout.Token);
            }

            releaseCaller.Set();
            caller.Join();
            await File.WriteAllTextAsync(Path.Combine(_workspace, "finish"), string.Empty, timeout.Token);
            var result = await execution.Result.WaitAsync(timeout.Token);

            _ = await Assert.That(result.ExitCode).IsEqualTo(7);
            _ = await Assert.That(result.Stdout).IsEqualTo("READYFINISHED");
            _ = await Assert.That(result.Stderr).IsEmpty();
        }
        finally
        {
            releaseCaller.Set();
            caller.Join();
        }
    }

    [Test]
    public async Task Pseudo_terminal_starts_in_the_real_sandbox(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var runner = ProcessRunner.Locate();

        if (!runner.SandboxAvailable)
        {
            return;
        }

        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        await using var execution = runner.Start(
            "printf pty-ready",
            ProcessEnvironmentOverrides.Empty,
            resources,
            Scratch(resources),
            WritableProfile(resources),
            ShellProcessTerminalMode.PseudoTerminal,
            cancellationToken);
        var result = await execution.Result;

        _ = await Assert.That(result.ExitCode).IsEqualTo(0);
        _ = await Assert.That(result.Stdout).Contains("pty-ready");
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

        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".test-state"),
                Path.Combine(_workspace, ".test-config"),
                Path.Combine(_workspace, ".test-data")),
            UserSessionId.Parse("session-test"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
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
        SecurityProfile.ForAgent(
            policy,
            resources.Workspace.WritableRoots,
            resources.ScratchRootDirectory,
            approvals);

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

        var path = Path.Combine(workspace, $"capturing-sandbox-{Guid.NewGuid():n}");
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

        var path = Path.Combine(workspace, $"sandbox-{Guid.NewGuid():n}");
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
