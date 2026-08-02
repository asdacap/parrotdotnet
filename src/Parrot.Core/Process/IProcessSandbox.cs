using Parrot.Permissions;
using Parrot.Security;
using Parrot.Store;

namespace Parrot.Process;

internal interface IProcessSandbox
{
    bool SandboxAvailable { get; }

    ShellProcessExecution Start(
        string command,
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources,
        AgentScratchDirectory scratch,
        SecurityProfile securityProfile,
        SandboxWriteGrantSnapshot writeGrants,
        ShellProcessTerminalMode terminalMode,
        CancellationToken cancellationToken);
}
