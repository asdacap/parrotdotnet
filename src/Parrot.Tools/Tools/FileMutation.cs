using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Parrot.Tools;

internal static partial class FileMutation
{
    public const string NoChanges = "No changes made.";

    private const int CurrentDirectoryFileDescriptor = -100;
    private const int WriteOnly = 0x1;
    private const int CreateFile = 0x40;
    private const int SymbolicLinkNoFollow = 0x100;
    private const int CloseOnExec = 0x80000;
    private const int EmptyPath = 0x1000;
    private const uint PrivateFileMode = 0x180;
    private const uint NoSymbolicLinks = 0x4;
    private const uint StatusType = 0x1;
    private const ushort FileTypeMask = 0xf000;
    private const ushort RegularFileType = 0x8000;
    private const ushort DirectoryFileType = 0x4000;
    private const ushort SymbolicLinkType = 0xa000;
    private const int MissingEntry = 2;
    private const int MissingParent = 20;

    public static void RequireRegularFileOrMissing(string path)
    {
        var kind = Inspect(path);
        if (kind is FileMutationEntryKind.Missing or FileMutationEntryKind.Regular)
        {
            return;
        }

        throw new InvalidOperationException("File mutations require regular files.");
    }

    public static void RequireRegularFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Source '{path}' is missing.");
        }

        RequireRegularFileOrMissing(path);
    }

    public static async Task Write(
        string path,
        byte[] data,
        bool createParents,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (createParents)
        {
            var parent = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException($"Destination '{path}' has no parent directory.");
            _ = Directory.CreateDirectory(parent);
        }

        RequireRegularFileOrMissing(path);
        var options = new FileStreamOptions
        {
            Access = FileAccess.Write,
            Mode = FileMode.OpenOrCreate,
            Options = FileOptions.Asynchronous,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = (UnixFileMode)PrivateFileMode;
        }

        await using var stream = OperatingSystem.IsLinux()
            ? OpenLinux(path, createParents)
            : new FileStream(path, options);
        RequireRegularFile(stream);
        stream.SetLength(0);
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    public static FileMutationEntryKind Inspect(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            return InspectLinux(path);
        }

        var information = new FileInfo(path);
        information.Refresh();
        var attributes = information.Attributes;
        if (attributes == (FileAttributes)(-1))
        {
            return FileMutationEntryKind.Missing;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return FileMutationEntryKind.SymbolicLink;
        }

        return (attributes & FileAttributes.Directory) != 0
            ? FileMutationEntryKind.Directory
            : FileMutationEntryKind.Regular;
    }

    private static FileStream OpenLinux(string path, bool create)
    {
        var specification = new LinuxOpenHow
        {
            Flags = (uint)(WriteOnly | CloseOnExec | (create ? CreateFile : 0)),
            Mode = create ? PrivateFileMode : 0,
            Resolve = NoSymbolicLinks,
        };
        var descriptor = OpenAt2(
            CurrentDirectoryFileDescriptor,
            path,
            in specification,
            (nuint)Marshal.SizeOf<LinuxOpenHow>());
        if (descriptor < 0)
        {
            throw new IOException(new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        }

        return OpenLinuxStream(descriptor);
    }

    private static FileStream OpenLinuxStream(long descriptor)
    {
        SafeFileHandle? handle = null;
        try
        {
            handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
            var stream = new FileStream(handle, FileAccess.Write, 4096, isAsync: false);
            handle = null;
            return stream;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static void RequireRegularFile(FileStream stream)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        if (Statx(
            stream.SafeFileHandle.DangerousGetHandle().ToInt32(),
            string.Empty,
            EmptyPath,
            StatusType,
            out var status) != 0)
        {
            throw new IOException(new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        }

        if ((status.Mode & FileTypeMask) != RegularFileType)
        {
            throw new InvalidOperationException("File mutations require regular files.");
        }
    }

    private static FileMutationEntryKind InspectLinux(string path)
    {
        if (Statx(
            CurrentDirectoryFileDescriptor,
            path,
            SymbolicLinkNoFollow,
            StatusType,
            out var status) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is MissingEntry or MissingParent)
            {
                return FileMutationEntryKind.Missing;
            }

            throw new IOException(new Win32Exception(error).Message);
        }

        return (status.Mode & FileTypeMask) switch
        {
            RegularFileType => FileMutationEntryKind.Regular,
            DirectoryFileType => FileMutationEntryKind.Directory,
            SymbolicLinkType => FileMutationEntryKind.SymbolicLink,
            _ => FileMutationEntryKind.Other,
        };
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "syscall", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial long OpenAt2(
        long number,
        int directoryFileDescriptor,
        string path,
        in LinuxOpenHow specification,
        nuint size);

    private static long OpenAt2(
        int directoryFileDescriptor,
        string path,
        in LinuxOpenHow specification,
        nuint size) => OpenAt2(437, directoryFileDescriptor, path, in specification, size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Statx(
        int directoryFileDescriptor,
        string path,
        int flags,
        uint mask,
        out LinuxStatx status);

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxOpenHow
    {
        public ulong Flags;

        public ulong Mode;

        public ulong Resolve;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(28)]
        public ushort Mode;
    }
}
