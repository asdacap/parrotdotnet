using Parrot.Security;
using Parrot.Store;

namespace Parrot.Process;

internal sealed class ProcessRunner(IProcessSandbox sandbox, SandboxGate sandboxGate)
{
    private const string SeatbeltExecutable = "/usr/bin/sandbox-exec";
    private readonly IProcessSandbox _unsandboxed = new UnsandboxedProcessSandbox();

    public SandboxGate SandboxGate { get; } = sandboxGate;

    public bool SandboxAvailable => sandbox.SandboxAvailable;

    public static ProcessRunner LocateConfigured(
        ExecutableLocator locator,
        SandboxGate sandboxGate,
        IReadOnlyList<string> devicePaths)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(sandboxGate);
        ArgumentNullException.ThrowIfNull(devicePaths);
        return OperatingSystem.IsLinux()
            ? new ProcessRunner(
                new LinuxBubblewrapSandbox(
                    locator.Locate("bwrap"),
                    requireTrustedPath: false,
                    new LinuxSandboxProcessLauncher(devicePaths)),
                sandboxGate)
            : LocateDefault(sandboxGate);
    }

    public IProcessExecution Start(
        string command,
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources,
        AgentScratchDirectory scratch,
        SecurityProfile securityProfile,
        ShellProcessTerminalMode terminalMode,
        CancellationToken cancellationToken) =>
        SelectSandbox().Start(
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

    private static ProcessRunner LocateDefault(SandboxGate sandboxGate)
    {
        if (OperatingSystem.IsMacOS())
        {
            return new ProcessRunner(
                new MacSeatbeltSandbox(SeatbeltExecutable, requireTrustedPath: true),
                sandboxGate);
        }

        return new ProcessRunner(new UnavailableProcessSandbox(), sandboxGate);
    }

    private IProcessSandbox SelectSandbox() => SandboxGate.Enabled ? sandbox : _unsandboxed;
}
