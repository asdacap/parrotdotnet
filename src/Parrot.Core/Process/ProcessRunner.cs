using System.Diagnostics;

namespace Parrot.Process;

// Runs shell commands under bubblewrap. The host filesystem is read-only, the
// working directory is writable, and there is no fallback: if bubblewrap is
// not available the command does not run (fail closed). That is the security
// property M3 exists to establish.
internal sealed class ProcessRunner(string bubblewrapPath)
{
    private const int MaxOutputCharacters = 64 << 10;

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

        var stdout = ReadBounded(process.StandardOutput);
        var stderr = ReadBounded(process.StandardError);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            _ = await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            throw;
        }

        var output = await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return new ProcessResult(
            process.ExitCode,
            output[0].Text,
            output[1].Text,
            output[0].Truncated,
            output[1].Truncated);
    }

    private static void Kill(System.Diagnostics.Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
    }

    private static async Task<BoundedOutput> ReadBounded(StreamReader reader)
    {
        var output = new System.Text.StringBuilder(MaxOutputCharacters);
        var buffer = new char[4096];
        var truncated = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            var remaining = MaxOutputCharacters - output.Length;

            if (remaining > 0)
            {
                _ = output.Append(buffer, 0, Math.Min(read, remaining));
            }

            truncated |= read > remaining;
        }

        return new BoundedOutput(output.ToString(), truncated);
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

    private sealed record BoundedOutput(string Text, bool Truncated);
}
