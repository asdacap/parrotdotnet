using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Parrot.Security;
using Parrot.Store;

namespace Parrot.Process;

internal sealed class LinuxSandboxProcessLauncher : ILinuxSandboxProcessLauncher
{
    public IProcessExecution StartPipe(
        string bubblewrapPath,
        string command,
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources,
        AgentScratchDirectory scratch,
        SecurityProfile securityProfile,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = bubblewrapPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in SandboxArguments(
                     command,
                     environment,
                     resources,
                     scratch,
                     securityProfile,
                     string.Empty))
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new System.Diagnostics.Process { StartInfo = startInfo };
        IProcessSignalTarget? signalTarget = null;
        PipeProcessOutputFiles? outputFiles = null;
        var started = false;
        var launcher = new PipeProcessLauncher(process);

        try
        {
            outputFiles = PipeProcessOutputFiles.Open(scratch.BlobDirectory);
            var startedTimestamp = Stopwatch.GetTimestamp();
            started = launcher.Start();
            signalTarget = LinuxProcessSignalTarget.Open(process);
            return new ShellProcessExecution(
                process,
                signalTarget,
                scratch.BlobDirectory,
                outputFiles,
                launcher.Completion,
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

            launcher.Join();

            process.Dispose();
            throw;
        }
    }

    public IProcessExecution StartPseudoTerminal(
        string bubblewrapPath,
        string command,
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources,
        AgentScratchDirectory scratch,
        SecurityProfile securityProfile,
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
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--bridge");
            startInfo.ArgumentList.Add($"/proc/{Environment.ProcessId}/fd/{slave}");
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add(bubblewrapPath);

            foreach (var argument in SandboxArguments(
                         command,
                         environment,
                         resources,
                         scratch,
                         securityProfile,
                         helperPath))
            {
                startInfo.ArgumentList.Add(argument);
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

    // Read-only host root first, followed by policy mounts in order.
    // --unshare-* and --cap-drop are the containment; --die-with-parent stops
    // an orphan outliving the turn.
    private static List<string> SandboxArguments(
        string command,
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources,
        AgentScratchDirectory scratch,
        SecurityProfile securityProfile,
        string pseudoTerminalHelperPath)
    {
        scratch.Provision();
        var arguments = new List<string>
        {
            "--die-with-parent",
            "--new-session",
            "--unshare-user",
            "--unshare-pid",
            "--unshare-ipc",
            "--unshare-uts",
            "--cap-drop", "ALL",
            "--ro-bind", "/", "/",
        };

        foreach (var entry in environment.Entries)
        {
            arguments.AddRange(["--setenv", entry.Key, entry.Value]);
        }

        AddSecurityRules(arguments, resources, securityProfile);
        if (pseudoTerminalHelperPath.Length > 0)
        {
            arguments.AddRange(["--ro-bind", pseudoTerminalHelperPath, pseudoTerminalHelperPath]);
        }

        // must be AFTER other mount
        arguments.AddRange(["--dev", "/dev", "--proc", "/proc"]);

        arguments.AddRange(["--chdir", resources.Workspace.LaunchDirectory, "--"]);

        if (pseudoTerminalHelperPath.Length > 0)
        {
            arguments.AddRange([pseudoTerminalHelperPath, "--attach", "--"]);
        }

        arguments.AddRange(["/bin/sh", "-c", command]);
        return arguments;
    }

    private static void AddSyntheticParents(
        List<string> arguments,
        UserSessionResources resources,
        string path)
    {
        var parent = Path.GetDirectoryName(path);
        var parents = new Stack<string>();

        while (parent is not null && resources.Owns(parent))
        {
            parents.Push(parent);
            parent = Path.GetDirectoryName(parent);
        }

        foreach (var directory in parents)
        {
            arguments.AddRange(["--dir", directory]);
        }
    }

    private static void AddSecurityRules(
        List<string> arguments,
        UserSessionResources resources,
        SecurityProfile securityProfile)
    {
        foreach (var rule in securityProfile.Materialize().Rules)
        {
            AddSyntheticParents(arguments, resources, rule.Path);

            if (!rule.Read)
            {
                AddReadMask(arguments, rule.Path);
            }
            else
            {
                arguments.AddRange([rule.Write ? "--bind" : "--ro-bind", rule.Path, rule.Path]);
            }
        }
    }

    private static void AddReadMask(List<string> arguments, string path)
    {
        if (Directory.Exists(path))
        {
            arguments.AddRange(["--tmpfs", path]);
        }
        else
        {
            arguments.AddRange(["--ro-bind", "/dev/null", path]);
        }
    }

    private sealed class PipeProcessLauncher(System.Diagnostics.Process process)
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Thread? _thread;

        public Task Completion => _completion.Task;

        public bool Start()
        {
            using var processStarted = new ManualResetEventSlim();
            ExceptionDispatchInfo? startupFailure = null;
            var started = false;
            var thread = new Thread(() =>
            {
                try
                {
                    started = process.Start();
                }
                catch (Exception exception)
                {
                    startupFailure = ExceptionDispatchInfo.Capture(exception);
                }
                finally
                {
                    processStarted.Set();
                }

                try
                {
                    if (started)
                    {
                        // Linux ties --die-with-parent to the launching OS thread, not the managed process.
                        process.WaitForExit();
                    }

                    _completion.SetResult();
                }
                catch (Exception exception)
                {
                    _completion.SetException(exception);
                }
            })
            {
                IsBackground = true,
                Name = "bubblewrap launcher",
            };
            thread.Start();
            _thread = thread;
            processStarted.Wait();
            startupFailure?.Throw();
            return started;
        }

        public void Join() => _thread?.Join();
    }
}
