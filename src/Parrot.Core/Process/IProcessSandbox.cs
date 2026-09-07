using Parrot.Security;
using Parrot.Store;

namespace Parrot.Process;

/// <summary>Launches shell commands under the supplied security profile using the host's sandbox facilities.</summary>
internal interface IProcessSandbox
{
    bool SandboxAvailable { get; }

    /// <summary>
    /// Starts an execution in the requested terminal mode and transfers its disposal responsibility to the caller.
    /// The cancellation token controls the execution lifetime; unavailable sandboxing or unsupported modes throw.
    /// </summary>
    ShellProcessExecution Start(
        string command,
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources,
        AgentScratchDirectory scratch,
        SecurityProfile securityProfile,
        ShellProcessTerminalMode terminalMode,
        CancellationToken cancellationToken);
}
