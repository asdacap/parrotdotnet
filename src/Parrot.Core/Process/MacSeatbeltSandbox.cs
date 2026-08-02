using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Parrot.Permissions;
using Parrot.Security;
using Parrot.Store;

namespace Parrot.Process;

internal sealed partial class MacSeatbeltSandbox : IProcessSandbox
{
    private const int WriteAccess = 2;
    private readonly string _seatbeltPath;

    [SupportedOSPlatform("macos")]
    public MacSeatbeltSandbox(string seatbeltPath) =>
        _seatbeltPath = ValidateSeatbeltPath(seatbeltPath, requireTrustedPath: false);

    [SupportedOSPlatform("macos")]
    internal MacSeatbeltSandbox(string seatbeltPath, bool requireTrustedPath) =>
        _seatbeltPath = ValidateSeatbeltPath(seatbeltPath, requireTrustedPath);

    public bool SandboxAvailable => _seatbeltPath.Length > 0;

    [SupportedOSPlatform("macos")]
    public ShellProcessExecution Start(
        string command,
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources,
        AgentScratchDirectory scratch,
        SecurityProfile securityProfile,
        SandboxWriteGrantSnapshot writeGrants,
        ShellProcessTerminalMode terminalMode,
        CancellationToken cancellationToken)
    {
        if (!SandboxAvailable)
        {
            throw new SandboxUnavailableException(
                "macOS Seatbelt is required but /usr/bin/sandbox-exec is unavailable");
        }

        if (terminalMode == ShellProcessTerminalMode.PseudoTerminal)
        {
            throw new PlatformNotSupportedException("Pseudo-terminal shell processes are not yet supported on macOS.");
        }

        if (terminalMode != ShellProcessTerminalMode.Pipe)
        {
            throw new ArgumentOutOfRangeException(nameof(terminalMode));
        }

        scratch.Provision();
        var profilePath = Path.Combine(resources.QueueDirectory, $"seatbelt-{Guid.NewGuid():n}.sb");
        WriteProfile(profilePath, CompilePolicy(resources, scratch, securityProfile, writeGrants));
        var startInfo = CreateStartInfo(_seatbeltPath, profilePath, command, environment, resources);
        var process = new System.Diagnostics.Process { StartInfo = startInfo };
        DarwinProcessSignalTarget? signalTarget = null;
        var started = false;

        try
        {
            started = process.Start();
            if (!started)
            {
                throw new SandboxUnavailableException("macOS Seatbelt did not start the shell process.");
            }

            signalTarget = DarwinProcessSignalTarget.Open(process);
            return new ShellProcessExecution(
                process,
                signalTarget,
                scratch.BlobDirectory,
                profilePath,
                cancellationToken);
        }
        catch
        {
            signalTarget?.Dispose();

            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            process.Dispose();
            File.Delete(profilePath);
            throw;
        }
    }

    internal static SeatbeltPolicySnapshot CompilePolicy(
        UserSessionResources resources,
        AgentScratchDirectory scratch,
        SecurityProfile securityProfile,
        SandboxWriteGrantSnapshot writeGrants)
    {
        var profile = new SeatbeltPolicy();
        profile.AllowWrite("/dev/null");

        if (!securityProfile.ReadOnly)
        {
            foreach (var path in WritableWorkspacePaths(resources.Workspace.LaunchDirectory))
            {
                profile.AllowWrite(path);
            }

            foreach (var target in writeGrants.CaptureValid())
            {
                profile.AllowWrite(target.Path);
            }
        }

        AddSecurityRules(profile, securityProfile);

        foreach (var root in resources.ProtectedRoots
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(path => path.Length))
        {
            profile.DenyRead(root);
        }

        profile.AllowWrite(scratch.Root);

        return profile.Capture();
    }

    internal static ProcessStartInfo CreateStartInfo(
        string seatbeltPath,
        string profilePath,
        string command,
        ProcessEnvironmentOverrides environment,
        UserSessionResources resources)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = seatbeltPath,
            WorkingDirectory = resources.Workspace.LaunchDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(profilePath);
        startInfo.ArgumentList.Add("/usr/bin/env");
        startInfo.ArgumentList.Add("-i");

        var childEnvironment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Select(entry => (Key: entry.Key as string, Value: entry.Value as string))
            .Where(entry => entry.Key is not null && entry.Value is not null)
            .ToDictionary(
                entry => entry.Key ?? string.Empty,
                entry => entry.Value ?? string.Empty,
                StringComparer.Ordinal);
        foreach (var entry in environment.Entries)
        {
            childEnvironment[entry.Key] = entry.Value;
        }

        foreach (var entry in childEnvironment.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            startInfo.ArgumentList.Add($"{entry.Key}={entry.Value}");
        }

        startInfo.ArgumentList.Add("/bin/sh");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(command);
        startInfo.Environment.Clear();
        startInfo.Environment["PATH"] = "/usr/bin:/bin";
        return startInfo;
    }

    private static void AddSecurityRules(SeatbeltPolicy profile, SecurityProfile securityProfile)
    {
        var applied = new List<SandboxRule>();

        foreach (var rule in securityProfile.Rules)
        {
            applied.Add(rule);
            AddEffectiveRule(profile, rule.Path, securityProfile.ReadOnly, applied);
        }
    }

    private static void AddEffectiveRule(
        SeatbeltPolicy profile,
        string path,
        bool readOnly,
        IEnumerable<SandboxRule> rules)
    {
        var effective = SecurityProfile.Compose(readOnly, rules, [], []);
        if (!effective.AllowsRead(path))
        {
            profile.DenyRead(path);
        }
        else if (effective.AllowsWrite(path))
        {
            profile.AllowWrite(path);
        }
        else
        {
            profile.AllowRead(path);
            profile.DenyWrite(path);
        }
    }

    private static IEnumerable<string> WritableWorkspacePaths(string workingDirectory)
    {
        var repositoryRoot = FindGitRepositoryRoot(workingDirectory);
        if (repositoryRoot is not null
            && !string.Equals(repositoryRoot, workingDirectory, StringComparison.Ordinal))
        {
            yield return repositoryRoot;
        }

        yield return workingDirectory;
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
        return Directory.Exists(commonDirectory)
               && string.Equals(Path.GetFileName(commonDirectory), ".git", StringComparison.Ordinal)
               && string.Equals(Path.GetDirectoryName(gitDirectory), worktreesDirectory, StringComparison.Ordinal)
            ? Path.GetDirectoryName(commonDirectory)
            : null;
    }

    private static string ResolvePath(string baseDirectory, string path) =>
        Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(baseDirectory, path));

    private static bool Contains(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    [SupportedOSPlatform("macos")]
    private static void EnsurePrivateDirectory(string directory)
    {
        _ = Directory.CreateDirectory(directory);
        File.SetUnixFileMode(
            directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [SupportedOSPlatform("macos")]
    private static void WriteProfile(string path, SeatbeltPolicySnapshot profile)
    {
        var options = new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.CreateNew,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };

        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(profile.Text);
    }

    [SupportedOSPlatform("macos")]
    private static string ValidateSeatbeltPath(string path, bool requireTrustedPath)
    {
        if (path.Length == 0)
        {
            return string.Empty;
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The Seatbelt path must be absolute.", nameof(path));
        }

        var canonical = Path.GetFullPath(path);
        var target = new FileInfo(canonical).ResolveLinkTarget(returnFinalTarget: true);
        if (target is not null)
        {
            canonical = Path.GetFullPath(target.FullName);
        }

        if (!File.Exists(canonical))
        {
            return string.Empty;
        }

        const UnixFileMode executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        if ((File.GetUnixFileMode(canonical) & executable) == 0)
        {
            return string.Empty;
        }

        if (requireTrustedPath && !HasTrustedParentChain(canonical))
        {
            throw new SandboxUnavailableException("sandbox-exec must be installed below a non-writable system path");
        }

        return canonical;
    }

    private static bool HasTrustedParentChain(string path)
    {
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
