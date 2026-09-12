using System.Runtime.InteropServices;
using Parrot.Security;
using Parrot.Store;

namespace Parrot.Process;

internal sealed partial class LinuxBubblewrapSandbox(
    string bubblewrapPath,
    bool requireTrustedPath,
    ILinuxSandboxProcessLauncher launcher) : IProcessSandbox
{
    private const int WriteAccess = 2;
    private readonly string _bubblewrapPath = ValidateBubblewrapPath(bubblewrapPath, requireTrustedPath);

    public bool SandboxAvailable => _bubblewrapPath.Length > 0;

    public IProcessExecution Start(
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
            ShellProcessTerminalMode.Pipe => launcher.StartPipe(
                _bubblewrapPath,
                command,
                environment,
                resources,
                scratch,
                securityProfile,
                cancellationToken),
            ShellProcessTerminalMode.PseudoTerminal => launcher.StartPseudoTerminal(
                _bubblewrapPath,
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
}
