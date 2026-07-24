using System.Diagnostics;

namespace Parrot.Process;

// Runs shell commands under bubblewrap. The host filesystem is read-only, the
// working directory is writable, and there is no fallback: if bubblewrap is
// not available the command does not run (fail closed). That is the security
// property M3 exists to establish.
internal sealed class ProcessRunner(string bubblewrapPath)
{
    // An empty path means bubblewrap was not found. Kept as a value rather than
    // a null so the fail-closed check is explicit.
    public bool SandboxAvailable => bubblewrapPath.Length > 0;

    public static ProcessRunner Locate() => new(FindOnPath("bwrap"));

    public async Task<ProcessResult> Run(
        string command, string workingDirectory, CancellationToken cancellationToken)
    {
        if (!SandboxAvailable)
        {
            throw new SandboxUnavailableException(
                "bubblewrap is required; install bwrap and enable unprivileged user namespaces");
        }

        using var process = new System.Diagnostics.Process
        {
            StartInfo = StartInfo(command, workingDirectory),
        };

        _ = process.Start();

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new ProcessResult(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false));
    }

    // Read-only host root first, then the writable working directory over it, so
    // the workspace is the one writable place. --unshare-* and --cap-drop are
    // the containment; --die-with-parent stops an orphan outliving the turn.
    private static IEnumerable<string> SandboxArguments(string command, string workingDirectory) =>
    [
        "--die-with-parent",
        "--new-session",
        "--unshare-user",
        "--unshare-pid",
        "--cap-drop", "ALL",
        "--ro-bind", "/", "/",
        "--dev", "/dev",
        "--proc", "/proc",
        "--tmpfs", "/tmp",
        "--bind", workingDirectory, workingDirectory,
        "--chdir", workingDirectory,
        "--", "/bin/sh", "-c", command,
    ];

    private static string FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private ProcessStartInfo StartInfo(string command, string workingDirectory)
    {
        var start = new ProcessStartInfo
        {
            FileName = bubblewrapPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in SandboxArguments(command, workingDirectory))
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }
}
