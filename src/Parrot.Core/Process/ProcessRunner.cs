using System.Diagnostics;
using System.Runtime.InteropServices;
using Parrot.Permissions;
using Parrot.Security;
using Parrot.Store;

namespace Parrot.Process;

// Runs shell commands under bubblewrap. The host filesystem is read-only, the
// working directory is writable, and there is no fallback: if bubblewrap is
// not available the command does not run (fail closed). That is the security
// property M3 exists to establish.
internal sealed partial class ProcessRunner(string bubblewrapPath)
{
    private const int MaxFormattedOutputBytes = 64 << 10;
    private const int MaxStreamOutputCharacters = 64 << 10;
    private const int WriteAccess = 2;

    private readonly string _bubblewrapPath = ValidateBubblewrapPath(bubblewrapPath, requireTrustedPath: false);

    // An empty path means bubblewrap was not found. Kept as a value rather than
    // a null so the fail-closed check is explicit.
    public bool SandboxAvailable => _bubblewrapPath.Length > 0;

    public static ProcessRunner Locate()
    {
        var path = FindOnPath("bwrap");
        return new ProcessRunner(ValidateBubblewrapPath(path, requireTrustedPath: true));
    }

    public static ProcessRunner Locate(ExecutableLocator locator) => new(locator.Locate("bwrap"));

    public async Task<ProcessResult> Run(
        string command,
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources,
        SecurityProfile securityProfile,
        SandboxWriteGrantSnapshot writeGrants,
        CancellationToken cancellationToken)
    {
        if (!SandboxAvailable)
        {
            throw new SandboxUnavailableException(
                "bubblewrap is required; install bwrap and enable unprivileged user namespaces");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _bubblewrapPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in SandboxArguments(command, environment, resources, securityProfile, writeGrants))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };
        _ = process.Start();

        var stdoutTask = ReadBounded(process.StandardOutput, resources.BlobDirectory);
        var stderrTask = ReadBounded(process.StandardError, resources.BlobDirectory);
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
                var result = new ProcessResult(process.ExitCode, stdout.Text, stderr.Text, string.Empty);

                if (System.Text.Encoding.UTF8.GetByteCount(ProcessResultFormatter.Format(result))
                    <= MaxFormattedOutputBytes)
                {
                    return result;
                }
            }

            var blobPath = await new ProcessOutputBlobStore(resources.BlobDirectory)
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
        var output = new System.Text.StringBuilder(MaxStreamOutputCharacters);
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);

            if (read == 0)
            {
                return new ProcessOutput(output.ToString(), string.Empty);
            }

            if (output.Length + read <= MaxStreamOutputCharacters)
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
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources,
        SecurityProfile securityProfile,
        SandboxWriteGrantSnapshot writeGrants)
    {
        PreparePrivateRuntime(resources);
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
            "--dev", "/dev",
            "--proc", "/proc",
        };

        foreach (var entry in environment.Entries)
        {
            arguments.AddRange(["--setenv", entry.Key, entry.Value]);
        }

        arguments.AddRange(["--bind", resources.TemporaryDirectory, "/tmp"]);

        if (!securityProfile.ReadOnly)
        {
            AddWritableWorkspace(arguments, resources.Workspace.LaunchDirectory);
            AddWriteGrants(arguments, writeGrants);
        }

        AddSecurityRules(arguments, securityProfile);
        AddProtectedRoots(arguments, resources);
        AddPrivateRuntime(arguments, resources);
        AddRuntimeCapabilities(arguments, securityProfile, resources);
        arguments.AddRange(
        [
            "--chdir", resources.Workspace.LaunchDirectory,
            "--", "/bin/sh", "-c", command,
        ]);
        return arguments;
    }

    private static void AddProtectedRoots(List<string> arguments, UserSessionResources resources)
    {
        foreach (var root in resources.ProtectedRoots
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(path => path.Length))
        {
            EnsurePrivateDirectory(root);
            AddReadMask(arguments, root);
        }
    }

    private static void AddPrivateRuntime(List<string> arguments, UserSessionResources resources)
    {
        arguments.AddRange(
        [
            "--bind", resources.RuntimeHomeDirectory, resources.RuntimeHomeDirectory,
            "--bind", resources.CacheDirectory, resources.CacheDirectory,
        ]);
    }

    private static void AddRuntimeCapabilities(
        List<string> arguments,
        SecurityProfile securityProfile,
        UserSessionResources resources)
    {
        AddSyntheticMount(arguments, resources, resources.BlobDirectory, write: false);

        foreach (var rule in securityProfile.RuntimeCapabilities)
        {
            var path = Path.GetFullPath(rule.Path);
            if (resources.Owns(path) && Path.Exists(path))
            {
                AddSyntheticMount(arguments, resources, path, rule.Action == SandboxRuleAction.AllowWrite);
            }
        }
    }

    private static void AddSyntheticMount(
        List<string> arguments,
        UserSessionResources resources,
        string path,
        bool write)
    {
        AddSyntheticParents(arguments, resources, path);
        arguments.AddRange([write ? "--bind" : "--ro-bind", path, path]);
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
            arguments.AddRange(["--dir", directory, "--chmod", "100", directory]);
        }

        if (parents.Count > 0)
        {
            arguments.AddRange(["--chmod", "100", resources.Root]);
        }
    }

    private static void AddWriteGrants(
        List<string> arguments,
        SandboxWriteGrantSnapshot writeGrants)
    {
        foreach (var target in writeGrants.CaptureValid())
        {
            arguments.AddRange(["--bind", target.Path, target.Path]);
        }
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
                arguments.AddRange([write ? "--bind" : "--ro-bind", path, path]);
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

    private static void AddReadMask(List<string> arguments, string path)
    {
        if (Directory.Exists(path))
        {
            arguments.AddRange(["--tmpfs", path, "--chmod", "100", path]);
        }
        else
        {
            arguments.AddRange(["--ro-bind", "/dev/null", path]);
        }
    }

    private static void PreparePrivateRuntime(UserSessionResources resources)
    {
        EnsurePrivateDirectory(resources.RuntimeHomeDirectory);
        EnsurePrivateDirectory(resources.CacheDirectory);
        EnsurePrivateDirectory(resources.TemporaryDirectory);
        EnsurePrivateDirectory(resources.BlobDirectory);
    }

    private static void EnsurePrivateDirectory(string directory)
    {
        _ = Directory.CreateDirectory(directory);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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
        var candidates = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathFullyQualified)
            .Select(directory => Path.Combine(directory, name))
            .Where(File.Exists)
            .ToArray();
        return candidates.FirstOrDefault(candidate => candidate.StartsWith("/nix/store/", StringComparison.Ordinal))
            ?? candidates.FirstOrDefault()
            ?? string.Empty;
    }

    private static string ValidateBubblewrapPath(string path, bool requireTrustedPath)
    {
        if (path.Length == 0)
        {
            return string.Empty;
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The bubblewrap path must be absolute.", nameof(path));
        }

        var canonical = Path.GetFullPath(path);
        var target = new FileInfo(canonical).ResolveLinkTarget(returnFinalTarget: true);
        if (target is not null)
        {
            canonical = Path.GetFullPath(target.FullName);
        }

        if (!File.Exists(canonical))
        {
            throw new FileNotFoundException("The bubblewrap executable does not exist.", canonical);
        }

        if (requireTrustedPath && !HasTrustedParentChain(canonical))
        {
            throw new SandboxUnavailableException("bubblewrap must be installed below a non-writable system path");
        }

        return canonical;
    }

    private static bool HasTrustedParentChain(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsFreeBSD())
        {
            return false;
        }

        for (var directory = new FileInfo(path).Directory; directory is not null; directory = directory.Parent)
        {
            if (Access(directory.FullName, WriteAccess) == 0)
            {
                return false;
            }
        }

        return true;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "access", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Access(string path, int mode);
}
