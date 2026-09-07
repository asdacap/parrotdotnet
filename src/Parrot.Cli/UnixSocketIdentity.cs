using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Parrot.Cli;

internal sealed partial record UnixSocketIdentity(ulong Device, ulong Inode)
{
    public static Socket Bind(EndPoint endpoint)
    {
        var descriptor = OpenSocket(1, 1, 0);
        if (descriptor < 0)
        {
            throw new SocketException(Marshal.GetLastPInvokeError());
        }

        var handle = (SafeSocketHandle?)new SafeSocketHandle(descriptor, ownsHandle: true);
        try
        {
            ArgumentNullException.ThrowIfNull(handle);
            if (SetDescriptorFlags(handle, 2, 1) != 0)
            {
                throw new SocketException(Marshal.GetLastPInvokeError());
            }

            var address = endpoint.Serialize();
            var bytes = new byte[address.Size];
            for (var index = 0; index < bytes.Length; index++)
            {
                bytes[index] = address[index];
            }

            if (BindSocket(handle, bytes, bytes.Length) != 0)
            {
                throw new SocketException(Marshal.GetLastPInvokeError());
            }

            var socket = new Socket(handle);
            handle = null;
            return socket;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    public static UnixSocketIdentity? Read(string path)
    {
        int result;
        ushort mode;
        UnixSocketIdentity identity;
        if (OperatingSystem.IsLinux())
        {
            result = Statx(-100, path, 0x100, 0x103, out var status);
            mode = status.Mode;
            identity = new(((ulong)status.DeviceMajor << 32) | status.DeviceMinor, status.Inode);
        }
        else if (OperatingSystem.IsMacOS())
        {
            result = LStat(path, out var status);
            mode = status.Mode;
            identity = new(unchecked((uint)status.Device), status.Inode);
        }
        else
        {
            throw new InvalidOperationException("Unix socket transport requires Linux or macOS");
        }

        if (result != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            return error is 2 or 20 ? null : throw new IOException(new Win32Exception(error).Message);
        }

        return (mode & 0xf000) == 0xc000
            ? identity
            : throw new InvalidOperationException($"transport path exists and is not a socket: {path}");
    }

    public void Remove(string path)
    {
        UnixSocketIdentity? current;
        try
        {
            current = Read(path);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (this == current)
        {
            File.Delete(path);
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int SetDescriptorFlags(SafeSocketHandle socket, int command, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "socket", SetLastError = true)]
    private static partial int OpenSocket(int domain, int type, int protocol);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "bind", SetLastError = true)]
    private static partial int BindSocket(SafeSocketHandle socket, byte[] address, int length);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Statx(int directory, string path, int flags, uint mask, out LinuxStatx status);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int LStat(string path, out DarwinStat status);

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(28)]
        public ushort Mode;

        [FieldOffset(32)]
        public ulong Inode;

        [FieldOffset(136)]
        public uint DeviceMajor;

        [FieldOffset(140)]
        public uint DeviceMinor;
    }

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct DarwinStat
    {
        [FieldOffset(0)]
        public int Device;

        [FieldOffset(4)]
        public ushort Mode;

        [FieldOffset(8)]
        public ulong Inode;
    }
}
