using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
        WriteProfile(profilePath, CompilePolicy(securityProfile));
        var startInfo = CreateStartInfo(_seatbeltPath, profilePath, command, environment, resources);
        var process = new System.Diagnostics.Process { StartInfo = startInfo };
        DarwinProcessSignalTarget? signalTarget = null;
        PipeProcessOutputFiles? outputFiles = null;
        var started = false;

        try
        {
            outputFiles = PipeProcessOutputFiles.Open(scratch.BlobDirectory);
            var startedTimestamp = Stopwatch.GetTimestamp();
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
                outputFiles,
                profilePath,
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
                process.WaitForExit();
            }

            process.Dispose();
            File.Delete(profilePath);
            throw;
        }
    }

    internal static SeatbeltPolicySnapshot CompilePolicy(SecurityProfile securityProfile)
    {
        var profile = new SeatbeltPolicy();
        profile.AllowWrite("/dev/null");

        AddSecurityRules(profile, securityProfile);

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
        foreach (var rule in securityProfile.Materialize().Rules)
        {
            if (!rule.Read)
            {
                profile.DenyRead(rule.Path);
            }
            else if (rule.Write)
            {
                profile.AllowWrite(rule.Path);
            }
            else
            {
                profile.AllowRead(rule.Path);
                profile.DenyWrite(rule.Path);
            }
        }
    }

    [SupportedOSPlatform("macos")]
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
