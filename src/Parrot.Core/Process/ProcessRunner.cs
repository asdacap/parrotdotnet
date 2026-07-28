using System.Diagnostics;
using Parrot.Security;

namespace Parrot.Process;

// Runs shell commands under bubblewrap. The host filesystem is read-only, the
// working directory is writable, and there is no fallback: if bubblewrap is
// not available the command does not run (fail closed). That is the security
// property M3 exists to establish.
internal sealed class ProcessRunner(string bubblewrapPath)
{
    private const int MaxOutputCharacters = 64 << 10;
    private const string HostRoot = "/tmp/.parrot-host-root";

    // An empty path means bubblewrap was not found. Kept as a value rather than
    // a null so the fail-closed check is explicit.
    public bool SandboxAvailable => bubblewrapPath.Length > 0;

    public static ProcessRunner Locate() => new(FindOnPath("bwrap"));

    public async Task<ProcessResult> Run(
        string command,
        string workingDirectory,
        string blobDirectory,
        SecurityProfile securityProfile,
        CancellationToken cancellationToken)
    {
        if (!SandboxAvailable)
        {
            throw new SandboxUnavailableException(
                "bubblewrap is required; install bwrap and enable unprivileged user namespaces");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = bubblewrapPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in SandboxArguments(command, workingDirectory, securityProfile))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
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
        var fileOptions = new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            Options = FileOptions.Asynchronous,
        };

        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using var stream = new FileStream(path, fileOptions);
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

    private static string TemporaryPath(string blobDirectory) =>
        Path.Combine(blobDirectory, $".process-{Guid.NewGuid():n}.tmp");

    // Read-only host root first, followed by policy mounts in order.
    // --unshare-* and --cap-drop are the containment; --die-with-parent stops
    // an orphan outliving the turn.
    private static List<string> SandboxArguments(
        string command,
        string workingDirectory,
        SecurityProfile securityProfile)
    {
        var arguments = new List<string>
        {
            "--die-with-parent",
            "--new-session",
            "--unshare-user",
            "--unshare-pid",
            "--cap-drop", "ALL",
            "--ro-bind", "/", "/",
            "--dev", "/dev",
            "--proc", "/proc",
            "--tmpfs", "/tmp",
            "--bind", "/", HostRoot,
        };

        if (!securityProfile.ReadOnly)
        {
            AddWritableUserDirectory(arguments, ".cache");
            AddWritableWorkspace(arguments, workingDirectory);
        }

        AddSecurityRules(arguments, securityProfile);
        arguments.AddRange(
        [
            "--tmpfs", HostRoot,
            "--chmod", "000", HostRoot,
            "--chdir", workingDirectory,
            "--", "/bin/sh", "-c", command,
        ]);
        return arguments;
    }

    private static void AddWritableWorkspace(List<string> arguments, string workingDirectory)
    {
        var repositoryRoot = FindGitRepositoryRoot(workingDirectory);

        if (repositoryRoot is not null
            && !string.Equals(repositoryRoot, workingDirectory, StringComparison.Ordinal))
        {
            arguments.AddRange(["--bind", repositoryRoot, repositoryRoot]);
        }

        arguments.AddRange(["--bind", workingDirectory, workingDirectory]);
    }

    private static void AddSecurityRules(List<string> arguments, SecurityProfile securityProfile)
    {
        var applied = new List<SandboxRule>();

        foreach (var rule in securityProfile.Rules)
        {
            applied.Add(rule);
            var path = Path.GetFullPath(rule.Path);
            var (read, write) = EvaluateAccess(path, securityProfile.ReadOnly, applied);

            if (!read)
            {
                AddReadMask(arguments, path);
            }
            else
            {
                arguments.AddRange([write ? "--bind" : "--ro-bind", HostPath(path), path]);
            }
        }
    }

    private static (bool Read, bool Write) EvaluateAccess(
        string path,
        bool readOnly,
        IEnumerable<SandboxRule> rules)
    {
        var profile = SecurityProfile.Compose(readOnly, rules, [], []);
        return (profile.AllowsRead(path), profile.AllowsWrite(path));
    }

    private static string HostPath(string path) =>
        path == Path.DirectorySeparatorChar.ToString()
            ? HostRoot
            : HostRoot + path;

    private static void AddReadMask(List<string> arguments, string path)
    {
        if (Directory.Exists(path))
        {
            arguments.AddRange(["--tmpfs", path, "--chmod", "000", path]);
        }
        else
        {
            arguments.AddRange(["--ro-bind", "/dev/null", path]);
        }
    }

    private static void AddWritableUserDirectory(List<string> arguments, string name)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (home.Length == 0)
        {
            return;
        }

        var directory = Path.Combine(home, name);
        _ = Directory.CreateDirectory(directory);
        arguments.AddRange(["--bind", directory, directory]);
    }

    private static string? FindGitRepositoryRoot(string workingDirectory)
    {
        try
        {
            for (var directory = new DirectoryInfo(Path.GetFullPath(workingDirectory));
                 directory is not null;
                 directory = directory.Parent)
            {
                var gitPath = Path.Combine(directory.FullName, ".git");

                if (Directory.Exists(gitPath))
                {
                    return directory.FullName;
                }

                if (File.Exists(gitPath))
                {
                    return FindLinkedRepositoryRoot(gitPath);
                }
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static string? FindLinkedRepositoryRoot(string gitPath)
    {
        var gitFile = File.ReadAllText(gitPath).Trim();

        if (!gitFile.StartsWith("gitdir: ", StringComparison.Ordinal))
        {
            return null;
        }

        var worktreeRoot = Path.GetDirectoryName(gitPath);

        if (worktreeRoot is null)
        {
            return null;
        }

        var gitDirectory = ResolvePath(worktreeRoot, gitFile[8..]);
        var commonDirectoryPath = Path.Combine(gitDirectory, "commondir");
        var backlinkPath = Path.Combine(gitDirectory, "gitdir");

        if (!File.Exists(commonDirectoryPath) || !File.Exists(backlinkPath))
        {
            return null;
        }

        var backlink = ResolvePath(gitDirectory, File.ReadAllText(backlinkPath).Trim());

        if (!string.Equals(backlink, Path.GetFullPath(gitPath), StringComparison.Ordinal))
        {
            return null;
        }

        var commonDirectory = Path.TrimEndingDirectorySeparator(
            ResolvePath(gitDirectory, File.ReadAllText(commonDirectoryPath).Trim()));
        var worktreesDirectory = Path.Combine(commonDirectory, "worktrees");

        if (!Directory.Exists(commonDirectory)
            || !string.Equals(Path.GetFileName(commonDirectory), ".git", StringComparison.Ordinal)
            || !string.Equals(Path.GetDirectoryName(gitDirectory), worktreesDirectory, StringComparison.Ordinal))
        {
            return null;
        }

        return Path.GetDirectoryName(commonDirectory);
    }

    private static string ResolvePath(string baseDirectory, string path) =>
        Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(baseDirectory, path));

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
}
