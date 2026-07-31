using System.Diagnostics;
using System.Runtime.InteropServices;
using Parrot.Permissions;
using Parrot.Security;
using Parrot.Store;

namespace Parrot.Process;

internal sealed partial class LinuxBubblewrapSandbox(string bubblewrapPath, bool requireTrustedPath) : IProcessSandbox
{
    private const int WriteAccess = 2;
    private const string SandboxHelperPath = "/run/parrot/parrot-pty-attach";
    private readonly string _bubblewrapPath = ValidateBubblewrapPath(bubblewrapPath, requireTrustedPath);

    public bool SandboxAvailable => _bubblewrapPath.Length > 0;

    public ShellProcessExecution Start(
        string command,
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources,
        SecurityProfile securityProfile,
        SandboxWriteGrantSnapshot writeGrants,
        ShellProcessTerminalMode terminalMode,
        CancellationToken cancellationToken)
    {
        if (!SandboxAvailable)
        {
            throw new SandboxUnavailableException(
                "bubblewrap is required; install bwrap and enable unprivileged user namespaces");
        }

        return terminalMode switch
        {
            ShellProcessTerminalMode.Pipe => StartPipe(
                command,
                environment,
                resources,
                securityProfile,
                writeGrants,
                cancellationToken),
            ShellProcessTerminalMode.PseudoTerminal => StartPseudoTerminal(
                command,
                environment,
                resources,
                securityProfile,
                writeGrants,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(terminalMode)),
        };
    }

    public async Task<ProcessResult> Run(
        string command,
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources,
        SecurityProfile securityProfile,
        SandboxWriteGrantSnapshot writeGrants,
        CancellationToken cancellationToken)
    {
        await using var execution = Start(
            command,
            environment,
            resources,
            securityProfile,
            writeGrants,
            ShellProcessTerminalMode.Pipe,
            cancellationToken);
        return await execution.Result.ConfigureAwait(false);
    }

    // Read-only host root first, followed by policy mounts in order.
    // --unshare-* and --cap-drop are the containment; --die-with-parent stops
    // an orphan outliving the turn.
    private static List<string> SandboxArguments(
        string command,
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources,
        SecurityProfile securityProfile,
        SandboxWriteGrantSnapshot writeGrants,
        string pseudoTerminalHelperPath)
    {
        PreparePrivateRuntime(resources);
        var arguments = new List<string>
        {
            "--die-with-parent",
            "--new-session",
            "--unshare-user",
            "--unshare-pid",
            "--unshare-ipc",
            "--unshare-uts",
            "--cap-drop", "ALL",
            "--ro-bind", "/", "/",
            "--dev", "/dev",
            "--proc", "/proc",
        };

        foreach (var entry in environment.Entries)
        {
            arguments.AddRange(["--setenv", entry.Key, entry.Value]);
        }

        arguments.AddRange(["--bind", resources.TemporaryDirectory, "/tmp"]);

        if (!securityProfile.ReadOnly)
        {
            AddWritableWorkspace(arguments, resources.Workspace.LaunchDirectory);
            AddWriteGrants(arguments, writeGrants);
        }

        AddSecurityRules(arguments, securityProfile);
        AddProtectedRoots(arguments, resources);
        AddPrivateRuntime(arguments, resources);
        AddRuntimeCapabilities(arguments, securityProfile, resources);
        AddRuntimeRestrictions(arguments, securityProfile, resources);

        if (pseudoTerminalHelperPath.Length > 0)
        {
            arguments.AddRange(["--ro-bind", pseudoTerminalHelperPath, SandboxHelperPath]);
        }

        arguments.AddRange(["--chdir", resources.Workspace.LaunchDirectory, "--"]);

        if (pseudoTerminalHelperPath.Length > 0)
        {
            arguments.AddRange([SandboxHelperPath, "--attach", "--"]);
        }

        arguments.AddRange(["/bin/sh", "-c", command]);
        return arguments;
    }

    private static void AddProtectedRoots(List<string> arguments, UserSessionResources resources)
    {
        foreach (var root in resources.ProtectedRoots
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(path => path.Length))
        {
            EnsurePrivateDirectory(root);
            AddReadMask(arguments, root);
        }
    }

    private static void AddPrivateRuntime(List<string> arguments, UserSessionResources resources)
    {
        arguments.AddRange(
        [
            "--bind", resources.RuntimeHomeDirectory, resources.RuntimeHomeDirectory,
            "--bind", resources.CacheDirectory, resources.CacheDirectory,
            "--bind", resources.TemporaryDirectory, resources.TemporaryDirectory,
        ]);
    }

    private static void AddRuntimeCapabilities(
        List<string> arguments,
        SecurityProfile securityProfile,
        UserSessionResources resources)
    {
        AddSyntheticMount(arguments, resources, resources.BlobDirectory, write: false);

        foreach (var rule in securityProfile.RuntimeCapabilities)
        {
            var path = Path.GetFullPath(rule.Path);
            if (resources.Owns(path) && Path.Exists(path))
            {
                AddSyntheticMount(arguments, resources, path, rule.Action == SandboxRuleAction.AllowWrite);
            }
        }
    }

    private static void AddRuntimeRestrictions(
        List<string> arguments,
        SecurityProfile securityProfile,
        UserSessionResources resources)
    {
        var mountRoots = securityProfile.RuntimeCapabilities
            .Select(rule => rule.Path)
            .Append(resources.RuntimeHomeDirectory)
            .Append(resources.CacheDirectory)
            .Append(resources.TemporaryDirectory)
            .ToArray();
        var applied = new List<SandboxRule>();

        foreach (var rule in securityProfile.Rules)
        {
            applied.Add(rule);
            var paths = mountRoots
                .Where(root => Contains(root, rule.Path) || Contains(rule.Path, root))
                .Select(root => Contains(rule.Path, root) ? root : rule.Path)
                .Distinct(StringComparer.Ordinal);

            foreach (var path in paths)
            {
                var (read, write) = EvaluateAccess(path, securityProfile.ReadOnly, applied);
                if (!read)
                {
                    AddReadMask(arguments, path);
                }
                else
                {
                    arguments.AddRange([write ? "--bind" : "--ro-bind", path, path]);
                }
            }
        }
    }

    private static void AddSyntheticMount(
        List<string> arguments,
        UserSessionResources resources,
        string path,
        bool write)
    {
        AddSyntheticParents(arguments, resources, path);
        arguments.AddRange([write ? "--bind" : "--ro-bind", path, path]);
    }

    private static void AddSyntheticParents(
        List<string> arguments,
        UserSessionResources resources,
        string path)
    {
        var parent = Path.GetDirectoryName(path);
        var parents = new Stack<string>();

        while (parent is not null && resources.Owns(parent))
        {
            parents.Push(parent);
            parent = Path.GetDirectoryName(parent);
        }

        foreach (var directory in parents)
        {
            arguments.AddRange(["--dir", directory, "--chmod", "100", directory]);
        }

        if (parents.Count > 0)
        {
            arguments.AddRange(["--chmod", "100", resources.Root]);
        }
    }

    private static void AddWriteGrants(
        List<string> arguments,
        SandboxWriteGrantSnapshot writeGrants)
    {
        foreach (var target in writeGrants.CaptureValid())
        {
            arguments.AddRange(["--bind", target.Path, target.Path]);
        }
    }

    private static void AddWritableWorkspace(List<string> arguments, string workingDirectory)
    {
        var repositoryRoot = FindGitRepositoryRoot(workingDirectory);

        if (repositoryRoot is not null
            && !string.Equals(repositoryRoot, workingDirectory, StringComparison.Ordinal))
        {
            arguments.AddRange(["--bind", repositoryRoot, repositoryRoot]);
        }

        arguments.AddRange(["--bind", workingDirectory, workingDirectory]);
    }

    private static void AddSecurityRules(List<string> arguments, SecurityProfile securityProfile)
    {
        var applied = new List<SandboxRule>();

        foreach (var rule in securityProfile.Rules)
        {
            applied.Add(rule);
            var path = Path.GetFullPath(rule.Path);
            var (read, write) = EvaluateAccess(path, securityProfile.ReadOnly, applied);

            if (!read)
            {
                AddReadMask(arguments, path);
            }
            else
            {
                arguments.AddRange([write ? "--bind" : "--ro-bind", path, path]);
            }
        }
    }

    private static (bool Read, bool Write) EvaluateAccess(
        string path,
        bool readOnly,
        IEnumerable<SandboxRule> rules)
    {
        var profile = SecurityProfile.Compose(readOnly, rules, [], []);
        return (profile.AllowsRead(path), profile.AllowsWrite(path));
    }

    private static bool Contains(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static void AddReadMask(List<string> arguments, string path)
    {
        if (Directory.Exists(path))
        {
            arguments.AddRange(["--tmpfs", path, "--chmod", "100", path]);
        }
        else
        {
            arguments.AddRange(["--ro-bind", "/dev/null", path]);
        }
    }

    private static void PreparePrivateRuntime(UserSessionResources resources)
    {
        EnsurePrivateDirectory(resources.RuntimeHomeDirectory);
        EnsurePrivateDirectory(resources.CacheDirectory);
        EnsurePrivateDirectory(resources.TemporaryDirectory);
        EnsurePrivateDirectory(resources.BlobDirectory);
    }

    private static void EnsurePrivateDirectory(string directory)
    {
        _ = Directory.CreateDirectory(directory);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static string? FindGitRepositoryRoot(string workingDirectory)
    {
        try
        {
            for (var directory = new DirectoryInfo(Path.GetFullPath(workingDirectory));
                 directory is not null;
                 directory = directory.Parent)
            {
                var gitPath = Path.Combine(directory.FullName, ".git");

                if (Directory.Exists(gitPath))
                {
                    return directory.FullName;
                }

                if (File.Exists(gitPath))
                {
                    return FindLinkedRepositoryRoot(gitPath);
                }
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static string? FindLinkedRepositoryRoot(string gitPath)
    {
        var gitFile = File.ReadAllText(gitPath).Trim();

        if (!gitFile.StartsWith("gitdir: ", StringComparison.Ordinal))
        {
            return null;
        }

        var worktreeRoot = Path.GetDirectoryName(gitPath);

        if (worktreeRoot is null)
        {
            return null;
        }

        var gitDirectory = ResolvePath(worktreeRoot, gitFile[8..]);
        var commonDirectoryPath = Path.Combine(gitDirectory, "commondir");
        var backlinkPath = Path.Combine(gitDirectory, "gitdir");

        if (!File.Exists(commonDirectoryPath) || !File.Exists(backlinkPath))
        {
            return null;
        }

        var backlink = ResolvePath(gitDirectory, File.ReadAllText(backlinkPath).Trim());

        if (!string.Equals(backlink, Path.GetFullPath(gitPath), StringComparison.Ordinal))
        {
            return null;
        }

        var commonDirectory = Path.TrimEndingDirectorySeparator(
            ResolvePath(gitDirectory, File.ReadAllText(commonDirectoryPath).Trim()));
        var worktreesDirectory = Path.Combine(commonDirectory, "worktrees");

        if (!Directory.Exists(commonDirectory)
            || !string.Equals(Path.GetFileName(commonDirectory), ".git", StringComparison.Ordinal)
            || !string.Equals(Path.GetDirectoryName(gitDirectory), worktreesDirectory, StringComparison.Ordinal))
        {
            return null;
        }

        return Path.GetDirectoryName(commonDirectory);
    }

    private static string ResolvePath(string baseDirectory, string path) =>
        Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(baseDirectory, path));

    private static string ValidateBubblewrapPath(string path, bool requireTrustedPath)
    {
        if (path.Length == 0)
        {
            return string.Empty;
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The bubblewrap path must be absolute.", nameof(path));
        }

        var canonical = Path.GetFullPath(path);
        var target = new FileInfo(canonical).ResolveLinkTarget(returnFinalTarget: true);
        if (target is not null)
        {
            canonical = Path.GetFullPath(target.FullName);
        }

        if (!File.Exists(canonical))
        {
            throw new FileNotFoundException("The bubblewrap executable does not exist.", canonical);
        }

        if (requireTrustedPath && !HasTrustedParentChain(canonical))
        {
            throw new SandboxUnavailableException("bubblewrap must be installed below a non-writable system path");
        }

        return canonical;
    }

    private static bool HasTrustedParentChain(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD())
        {
            return false;
        }

        for (var directory = new FileInfo(path).Directory; directory is not null; directory = directory.Parent)
        {
            if (Access(directory.FullName, WriteAccess) == 0)
            {
                return false;
            }
        }

        return true;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "access", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Access(string path, int mode);

    private static LinuxProcessSignalTarget OpenSignalTarget(System.Diagnostics.Process process) =>
        LinuxProcessSignalTarget.Open(process);

    private ShellProcessExecution StartPipe(
        string command,
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources,
        SecurityProfile securityProfile,
        SandboxWriteGrantSnapshot writeGrants,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _bubblewrapPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in SandboxArguments(
                     command,
                     environment,
                     resources,
                     securityProfile,
                     writeGrants,
                     string.Empty))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new System.Diagnostics.Process { StartInfo = startInfo };
        LinuxProcessSignalTarget? signalTarget = null;
        var started = false;

        try
        {
            started = process.Start();
            signalTarget = OpenSignalTarget(process);
            return new ShellProcessExecution(process, signalTarget, resources.BlobDirectory, cancellationToken);
        }
        catch
        {
            signalTarget?.Dispose();

            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            process.Dispose();
            throw;
        }
    }

    private ShellProcessExecution StartPseudoTerminal(
        string command,
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources,
        SecurityProfile securityProfile,
        SandboxWriteGrantSnapshot writeGrants,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Pseudo-terminal shell processes require Linux.");
        }

        var helperPath = Path.Combine(AppContext.BaseDirectory, "parrot-pty-attach");

        if (!File.Exists(helperPath))
        {
            throw new FileNotFoundException("The PTY attach helper does not exist.", helperPath);
        }

        var (master, slave) = LinuxPseudoTerminal.Open();
        System.Diagnostics.Process? process = null;
        LinuxProcessSignalTarget? signalTarget = null;
        var started = false;

        try
        {
            LinuxPseudoTerminal.Resize(master, 24, 80);
            var startInfo = new ProcessStartInfo
            {
                FileName = helperPath,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--bridge");
            startInfo.ArgumentList.Add($"/proc/{Environment.ProcessId}/fd/{slave}");
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add(_bubblewrapPath);

            foreach (var argument in SandboxArguments(
                         command,
                         environment,
                         resources,
                         securityProfile,
                         writeGrants,
                         helperPath))
            {
                startInfo.ArgumentList.Add(argument);
            }

            process = new System.Diagnostics.Process { StartInfo = startInfo };
            started = process.Start();
            signalTarget = OpenSignalTarget(process);
            return new ShellProcessExecution(
                process,
                signalTarget,
                master,
                slave,
                resources.BlobDirectory,
                cancellationToken);
        }
        catch
        {
            signalTarget?.Dispose();

            if (started && process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            process?.Dispose();
            LinuxPseudoTerminal.Close(master);
            LinuxPseudoTerminal.Close(slave);
            throw;
        }
    }
}
