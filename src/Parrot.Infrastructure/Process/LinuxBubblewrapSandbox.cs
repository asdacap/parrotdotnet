using Parrot.Security;
using Parrot.Store;

namespace Parrot.Process;

internal sealed class LinuxBubblewrapSandbox(
    string bubblewrapPath,
    bool requireTrustedPath,
    ILinuxSandboxProcessLauncher launcher) : IProcessSandbox
{
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

        var canonical = SandboxExecutablePath.Canonicalize(path, "bubblewrap");

        if (!File.Exists(canonical))
        {
            throw new FileNotFoundException("The bubblewrap executable does not exist.", canonical);
        }

        if (requireTrustedPath && !SandboxExecutablePath.HasTrustedParentChain(canonical))
        {
            throw new SandboxUnavailableException("bubblewrap must be installed below a non-writable system path");
        }

        return canonical;
    }
}
