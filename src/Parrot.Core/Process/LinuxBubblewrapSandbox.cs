using System.Diagnostics;
using System.Runtime.InteropServices;
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
        AgentScratchDirectory scratch,
        SecurityProfile securityProfile,
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
                scratch,
                securityProfile,
                cancellationToken),
            ShellProcessTerminalMode.PseudoTerminal => StartPseudoTerminal(
                command,
                environment,
                resources,
                scratch,
                securityProfile,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(terminalMode)),
        };
    }

    public async Task<ProcessResult> Run(
        string command,
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources,
        AgentScratchDirectory scratch,
        SecurityProfile securityProfile,
        CancellationToken cancellationToken)
    {
        await using var execution = Start(
            command,
            environment,
            resources,
            scratch,
            securityProfile,
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
        AgentScratchDirectory scratch,
        SecurityProfile securityProfile,
        string pseudoTerminalHelperPath)
    {
        scratch.Provision();
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
        };

        foreach (var entry in environment.Entries)
        {
            arguments.AddRange(["--setenv", entry.Key, entry.Value]);
        }

        AddSecurityRules(arguments, resources, securityProfile);
        if (pseudoTerminalHelperPath.Length > 0)
        {
            arguments.AddRange(["--ro-bind", pseudoTerminalHelperPath, SandboxHelperPath]);
        }

        // must be AFTER other mount
        arguments.AddRange(["--dev", "/dev", "--proc", "/proc"]);

        arguments.AddRange(["--chdir", resources.Workspace.LaunchDirectory, "--"]);

        if (pseudoTerminalHelperPath.Length > 0)
        {
            arguments.AddRange([SandboxHelperPath, "--attach", "--"]);
        }

        arguments.AddRange(["/bin/sh", "-c", command]);
        return arguments;
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
            arguments.AddRange(["--dir", directory]);
        }
    }

    private static void AddSecurityRules(
        List<string> arguments,
        UserSessionResources resources,
        SecurityProfile securityProfile)
    {
        var applied = new List<SandboxRule>();

        foreach (var rule in securityProfile.Rules)
        {
            applied.Add(rule);
            var path = Path.GetFullPath(rule.Path);
            AddSyntheticParents(arguments, resources, path);
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
            arguments.AddRange(["--tmpfs", path]);
        }
        else
        {
            arguments.AddRange(["--ro-bind", "/dev/null", path]);
        }
    }

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
        AgentScratchDirectory scratch,
        SecurityProfile securityProfile,
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
                     scratch,
                     securityProfile,
                     string.Empty))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new System.Diagnostics.Process { StartInfo = startInfo };
        LinuxProcessSignalTarget? signalTarget = null;
        var started = false;

        try
        {
            var startedTimestamp = Stopwatch.GetTimestamp();
            started = process.Start();
            signalTarget = OpenSignalTarget(process);
            return new ShellProcessExecution(
                process,
                signalTarget,
                scratch.BlobDirectory,
                startedTimestamp,
                cancellationToken);
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
        AgentScratchDirectory scratch,
        SecurityProfile securityProfile,
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
                         scratch,
                         securityProfile,
                         helperPath))
            {
                startInfo.ArgumentList.Add(argument);
            }

            process = new System.Diagnostics.Process { StartInfo = startInfo };
            var startedTimestamp = Stopwatch.GetTimestamp();
            started = process.Start();
            signalTarget = OpenSignalTarget(process);
            return new ShellProcessExecution(
                process,
                signalTarget,
                master,
                slave,
                scratch.BlobDirectory,
                startedTimestamp,
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
