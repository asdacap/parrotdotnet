using System.Runtime.InteropServices;

namespace Parrot.Process;

internal static partial class SandboxExecutablePath
{
    private const int WriteAccess = 2;

    public static string Canonicalize(string path, string executableName)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"The {executableName} path must be absolute.", nameof(path));
        }

        var canonical = Path.GetFullPath(path);
        var target = new FileInfo(canonical).ResolveLinkTarget(returnFinalTarget: true);
        return target is null ? canonical : Path.GetFullPath(target.FullName);
    }

    public static bool HasTrustedParentChain(string path)
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
