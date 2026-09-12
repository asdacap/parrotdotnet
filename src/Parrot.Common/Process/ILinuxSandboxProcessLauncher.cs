using Parrot.Security;
using Parrot.Store;

namespace Parrot.Process;

/// <summary>Launches Linux sandbox executions and transfers their resource ownership to the caller.</summary>
internal interface ILinuxSandboxProcessLauncher
{
    /// <summary>Starts a pipe execution on a launcher thread retained until exit, cleaning up failed startup resources.</summary>
    IProcessExecution StartPipe(
        string bubblewrapPath,
        string command,
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources,
        AgentScratchDirectory scratch,
        SecurityProfile securityProfile,
        CancellationToken cancellationToken);

    /// <summary>Starts a Linux PTY bridge execution, cleaning up descriptors and process resources if startup fails.</summary>
    IProcessExecution StartPseudoTerminal(
        string bubblewrapPath,
        string command,
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources,
        AgentScratchDirectory scratch,
        SecurityProfile securityProfile,
        CancellationToken cancellationToken);
}
