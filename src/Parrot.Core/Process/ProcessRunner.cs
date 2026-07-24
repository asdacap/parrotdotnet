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
        string command,
        string workingDirectory,
        string blobDirectory,
        CancellationToken cancellationToken)
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

        var stdoutTask = ReadBounded(process.StandardOutput, blobDirectory);
        var stderrTask = ReadBounded(process.StandardError, blobDirectory);
        var exitTask = process.WaitForExitAsync(cancellationToken);
        ProcessOutput[] output;

        try
        {
            var pending = new List<Task> { exitTask, stdoutTask, stderrTask };

            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                _ = pending.Remove(completed);
                await completed.ConfigureAwait(false);
            }

            output = await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await DrainAndDelete(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        var stdout = output[0];
        var stderr = output[1];

        try
        {
            if (!stdout.Spilled && !stderr.Spilled)
            {
                return new ProcessResult(process.ExitCode, stdout.Text, stderr.Text, string.Empty);
            }

            var blobPath = await new ProcessOutputBlobStore(blobDirectory)
                .Persist(process.ExitCode, stdout, stderr, cancellationToken)
                .ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, string.Empty, string.Empty, blobPath);
        }
        finally
        {
            stdout.DeleteTemporaryFile();
            stderr.DeleteTemporaryFile();
        }
    }

    private static void Kill(System.Diagnostics.Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
    }

    private static async Task<ProcessOutput> ReadBounded(StreamReader reader, string blobDirectory)
    {
        var output = new System.Text.StringBuilder(MaxOutputCharacters);
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);

            if (read == 0)
            {
                return new ProcessOutput(output.ToString(), string.Empty);
            }

            if (output.Length + read <= MaxOutputCharacters)
            {
                _ = output.Append(buffer, 0, read);
                continue;
            }

            ProcessOutputBlobStore.EnsureDirectory(blobDirectory);
            var temporaryPath = TemporaryPath(blobDirectory);

            try
            {
                await Spill(reader, output, buffer.AsMemory(0, read), temporaryPath).ConfigureAwait(false);
                return new ProcessOutput(string.Empty, temporaryPath);
            }
            catch
            {
                File.Delete(temporaryPath);
                throw;
            }
        }
    }

    private static async Task DrainAndDelete(params Task<ProcessOutput>[] outputs)
    {
        try
        {
            var completed = await Task.WhenAll(outputs).ConfigureAwait(false);

            foreach (var output in completed)
            {
                output.DeleteTemporaryFile();
            }
        }
        catch
        {
            foreach (var task in outputs)
            {
                try
                {
                    var output = await task.ConfigureAwait(false);
                    output.DeleteTemporaryFile();
                }
                catch
                {
                }
            }
        }
    }

    private static async Task Spill(
        StreamReader reader,
        System.Text.StringBuilder initial,
        ReadOnlyMemory<char> firstOverflow,
        string path)
    {
        await using var stream = new FileStream(path, TemporaryFileOptions());
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(initial.ToString()).ConfigureAwait(false);
        await writer.WriteAsync(firstOverflow).ConfigureAwait(false);
        var buffer = new char[4096];

        while (true)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);

            if (read == 0)
            {
                return;
            }

            await writer.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }
    }

    private static FileStreamOptions TemporaryFileOptions()
    {
        var options = new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Options = FileOptions.Asynchronous,
        };

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return options;
    }

    private static string TemporaryPath(string blobDirectory) =>
        Path.Combine(blobDirectory, $".process-{Guid.NewGuid():n}.tmp");

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
