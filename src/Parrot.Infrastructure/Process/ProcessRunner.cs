using Parrot.Security;
using Parrot.Store;

namespace Parrot.Process;

internal sealed class ProcessRunner
{
    private const string SeatbeltExecutable = "/usr/bin/sandbox-exec";
    private readonly IProcessSandbox _sandbox;

    public ProcessRunner(string bubblewrapPath) =>
        _sandbox = new LinuxBubblewrapSandbox(bubblewrapPath, requireTrustedPath: false, new LinuxSandboxProcessLauncher());

    internal ProcessRunner(IProcessSandbox sandbox) => _sandbox = sandbox;

    public bool SandboxAvailable => _sandbox.SandboxAvailable;

    public static ProcessRunner Locate()
    {
        if (OperatingSystem.IsLinux())
        {
            return new ProcessRunner(new LinuxBubblewrapSandbox(
                ExecutableLocator.Capture().Locate("bwrap"),
                requireTrustedPath: true,
                new LinuxSandboxProcessLauncher()));
        }

        if (OperatingSystem.IsMacOS())
        {
            return new ProcessRunner(new MacSeatbeltSandbox(SeatbeltExecutable, requireTrustedPath: true));
        }

        return new ProcessRunner(new UnavailableProcessSandbox());
    }

    public static ProcessRunner Locate(ExecutableLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        return OperatingSystem.IsLinux()
            ? new ProcessRunner(new LinuxBubblewrapSandbox(locator.Locate("bwrap"), requireTrustedPath: false, new LinuxSandboxProcessLauncher()))
            : Locate();
    }

    public IProcessExecution Start(
        string command,
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources,
        AgentScratchDirectory scratch,
        SecurityProfile securityProfile,
        ShellProcessTerminalMode terminalMode,
        CancellationToken cancellationToken) =>
        _sandbox.Start(
            command,
            environment,
            resources,
            scratch,
            securityProfile,
            terminalMode,
            cancellationToken);

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
}
