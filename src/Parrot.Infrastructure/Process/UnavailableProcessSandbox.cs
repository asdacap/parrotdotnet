using Parrot.Security;
using Parrot.Store;

namespace Parrot.Process;

internal sealed class UnavailableProcessSandbox : IProcessSandbox
{
    public bool SandboxAvailable => false;

    public IProcessExecution Start(
        string command,
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources,
        AgentScratchDirectory scratch,
        SecurityProfile securityProfile,
        ShellProcessTerminalMode terminalMode,
        CancellationToken cancellationToken) =>
        throw new SandboxUnavailableException("Sandboxed shell commands require Linux or macOS.");
}
