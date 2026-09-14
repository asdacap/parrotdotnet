using System.Runtime.InteropServices;

namespace Parrot.Queues;

internal static partial class QueueNative
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int Link(string existingPath, string newPath);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "mkdir", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int MakeDirectory(string path, uint mode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", EntryPoint = "CreateDirectoryW", StringMarshalling = StringMarshalling.Utf16,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CreateDirectory(string path, nint securityAttributes);
}
