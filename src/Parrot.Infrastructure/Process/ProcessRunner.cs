using Parrot.Security;
using Parrot.Store;

namespace Parrot.Process;

internal sealed class ProcessRunner
{
    private const string SeatbeltExecutable = "/usr/bin/sandbox-exec";
    private readonly IProcessSandbox _sandbox;
    private readonly IProcessSandbox _unsandboxed;

    public ProcessRunner(string bubblewrapPath)
        : this(bubblewrapPath, requireTrustedPath: false, new SandboxGate(enabled: true))
    {
    }

    public ProcessRunner(string bubblewrapPath, bool requireTrustedPath, SandboxGate sandboxGate)
    {
        _sandbox = new LinuxBubblewrapSandbox(bubblewrapPath, requireTrustedPath, new LinuxSandboxProcessLauncher());
        _unsandboxed = new UnsandboxedProcessSandbox();
        SandboxGate = sandboxGate;
    }

    internal ProcessRunner(IProcessSandbox sandbox)
        : this(sandbox, new SandboxGate(enabled: true))
    {
    }

    internal ProcessRunner(IProcessSandbox sandbox, SandboxGate sandboxGate)
    {
        _sandbox = sandbox;
        _unsandboxed = new UnsandboxedProcessSandbox();
        SandboxGate = sandboxGate;
    }

    public SandboxGate SandboxGate { get; }

    public bool SandboxAvailable => _sandbox.SandboxAvailable;

    public static ProcessRunner Locate() => Locate(new SandboxGate(enabled: true));

    public static ProcessRunner Locate(SandboxGate sandboxGate)
    {
        ArgumentNullException.ThrowIfNull(sandboxGate);
        if (OperatingSystem.IsLinux())
        {
            return new ProcessRunner(
                new LinuxBubblewrapSandbox(
                    ExecutableLocator.Capture().Locate("bwrap"),
                    requireTrustedPath: true,
                    new LinuxSandboxProcessLauncher()),
                sandboxGate);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new ProcessRunner(
                new MacSeatbeltSandbox(SeatbeltExecutable, requireTrustedPath: true),
                sandboxGate);
        }

        return new ProcessRunner(new UnavailableProcessSandbox(), sandboxGate);
    }

    public static ProcessRunner Locate(ExecutableLocator locator) =>
        Locate(locator, new SandboxGate(enabled: true));

    public static ProcessRunner Locate(ExecutableLocator locator, SandboxGate sandboxGate)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(sandboxGate);
        return OperatingSystem.IsLinux()
            ? new ProcessRunner(
                new LinuxBubblewrapSandbox(
                    locator.Locate("bwrap"),
                    requireTrustedPath: false,
                    new LinuxSandboxProcessLauncher()),
                sandboxGate)
            : Locate(sandboxGate);
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

    private IProcessSandbox SelectSandbox() => SandboxGate.Enabled ? _sandbox : _unsandboxed;
}
