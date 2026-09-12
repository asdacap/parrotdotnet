using System.Runtime.Versioning;

namespace Parrot.Process;

internal sealed class ExecutableLocator(string path, string pathExt)
{
    private const string DefaultWindowsPathExtensions = ".COM;.EXE;.BAT;.CMD";

    private readonly string[] _directories = path.Split(
        Path.PathSeparator,
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private readonly string[] _windowsExtensions =
    [
        .. pathExt
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeWindowsExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    public static ExecutableLocator Capture()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var pathExtValue = Environment.GetEnvironmentVariable("PATHEXT") ?? DefaultWindowsPathExtensions;
        return new ExecutableLocator(pathValue, pathExtValue);
    }

    public string Locate(string command)
    {
        if (ContainsDirectorySeparator(command))
        {
            return FindExecutable(command);
        }

        foreach (var directory in _directories)
        {
            string candidate;

            try
            {
                candidate = Path.Combine(NormalizeDirectory(directory), command);
            }
            catch (Exception failure) when (failure is ArgumentException or NotSupportedException)
            {
                continue;
            }

            var executable = FindExecutable(candidate);

            if (executable.Length > 0)
            {
                return executable;
            }
        }

        return string.Empty;
    }

    [UnsupportedOSPlatform("windows")]
    private static bool IsUnixExecutable(string candidate)
    {
        try
        {
            if (!File.Exists(candidate))
            {
                return false;
            }

            const UnixFileMode executeBits = UnixFileMode.UserExecute
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(candidate) & executeBits) != 0;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool ContainsDirectorySeparator(string command) =>
        command.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || command.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

    private static string NormalizeDirectory(string directory)
    {
        if (OperatingSystem.IsWindows()
            && directory.Length >= 2
            && directory[0] == '"'
            && directory[^1] == '"')
        {
            return directory[1..^1];
        }

        return directory;
    }

    private static string NormalizeWindowsExtension(string extension) =>
        extension[0] == '.' ? extension : "." + extension;

    private string FindExecutable(string candidate)
    {
        if (!OperatingSystem.IsWindows())
        {
            return IsUnixExecutable(candidate) ? candidate : string.Empty;
        }

        if (Path.HasExtension(candidate))
        {
            var extension = Path.GetExtension(candidate);
            return _windowsExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) && File.Exists(candidate)
                ? candidate
                : string.Empty;
        }

        foreach (var extension in _windowsExtensions)
        {
            var extendedCandidate = candidate + extension;

            if (File.Exists(extendedCandidate))
            {
                return extendedCandidate;
            }
        }

        return string.Empty;
    }
}
