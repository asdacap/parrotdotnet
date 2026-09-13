using System.Diagnostics;
using Parrot.Security;
using Parrot.Store;

namespace Parrot.Process;

// Runs shell commands without OS confinement. The sandbox gate is the only
// caller; commands still use the same pipe and PTY plumbing so output capture,
// spill, and cancellation behave exactly as the sandboxed paths. The PTY path
// keeps the parrot-pty-attach bridge protocol but execs /bin/sh directly
// instead of bubblewrap.
internal sealed class UnsandboxedProcessSandbox : IProcessSandbox
{
    private const string Shell = "/bin/sh";

    public bool SandboxAvailable => true;

    public IProcessExecution Start(
        string command,
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources,
        AgentScratchDirectory scratch,
        SecurityProfile securityProfile,
        ShellProcessTerminalMode terminalMode,
        CancellationToken cancellationToken)
    {
        scratch.Provision();
        _ = Directory.CreateDirectory(resources.QueueDirectory);
        return terminalMode switch
        {
            ShellProcessTerminalMode.Pipe => StartPipe(
                command,
                environment,
                resources,
                scratch,
                cancellationToken),
            ShellProcessTerminalMode.PseudoTerminal => StartPseudoTerminal(
                command,
                environment,
                resources,
                scratch,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(terminalMode)),
        };
    }

    private static ShellProcessExecution StartPipe(
        string command,
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources,
        AgentScratchDirectory scratch,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Shell,
            WorkingDirectory = resources.Workspace.LaunchDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        foreach (var entry in environment.Entries)
        {
            startInfo.Environment[entry.Key] = entry.Value;
        }

        var process = new System.Diagnostics.Process { StartInfo = startInfo };
        IProcessSignalTarget? signalTarget = null;
        PipeProcessOutputFiles? outputFiles = null;
        var started = false;

        try
        {
            outputFiles = PipeProcessOutputFiles.Open(scratch.BlobDirectory);
            var startedTimestamp = Stopwatch.GetTimestamp();
            started = process.Start();
            signalTarget = LinuxProcessSignalTarget.Open(process);
            return new ShellProcessExecution(
                process,
                signalTarget,
                scratch.BlobDirectory,
                outputFiles,
                Task.CompletedTask,
                startedTimestamp,
                cancellationToken);
        }
        catch
        {
            signalTarget?.Dispose();

            outputFiles?.DeleteStartupArtifacts();

            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.Dispose();
            throw;
        }
    }

    private static ShellProcessExecution StartPseudoTerminal(
        string command,
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources,
        AgentScratchDirectory scratch,
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
        IProcessSignalTarget? signalTarget = null;
        var started = false;

        try
        {
            LinuxPseudoTerminal.Resize(master, 24, 80);
            var startInfo = new ProcessStartInfo
            {
                FileName = helperPath,
                WorkingDirectory = resources.Workspace.LaunchDirectory,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--bridge");
            startInfo.ArgumentList.Add($"/proc/{Environment.ProcessId}/fd/{slave}");
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add(Shell);
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
            foreach (var entry in environment.Entries)
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }

            process = new System.Diagnostics.Process { StartInfo = startInfo };
            var startedTimestamp = Stopwatch.GetTimestamp();
            started = process.Start();
            signalTarget = LinuxProcessSignalTarget.Open(process);
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
