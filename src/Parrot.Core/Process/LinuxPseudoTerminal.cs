using System.Runtime.InteropServices;
using System.Text;

namespace Parrot.Process;

internal static partial class LinuxPseudoTerminal
{
    private const int ReadWrite = 2;
    private const int NoControllingTerminal = 0x100;
    private const int CloseOnExec = 0x80000;
    private const int InterruptedSystemCall = 4;
    private const int InputOutputError = 5;
    private const nuint SetWindowSize = 0x5414;

    public static (int Master, int Slave) Open()
    {
        var master = OpenPseudoTerminal(ReadWrite | NoControllingTerminal | CloseOnExec);

        if (master < 0)
        {
            throw Failure("open PTY master");
        }

        var slave = -1;

        try
        {
            if (GrantPseudoTerminal(master) != 0)
            {
                throw Failure("grant PTY slave");
            }

            if (UnlockPseudoTerminal(master) != 0)
            {
                throw Failure("unlock PTY slave");
            }

            var pathBuffer = new byte[4096];
            var pathResult = GetPseudoTerminalName(master, pathBuffer, (nuint)pathBuffer.Length);

            if (pathResult != 0)
            {
                throw new IOException($"failed to resolve PTY slave (errno {pathResult})");
            }

            var terminator = Array.IndexOf(pathBuffer, (byte)0);

            if (terminator <= 0)
            {
                throw new IOException("failed to resolve PTY slave");
            }

            var path = Encoding.UTF8.GetString(pathBuffer, 0, terminator);
            slave = OpenDescriptor(path, ReadWrite | NoControllingTerminal | CloseOnExec);

            if (slave < 0)
            {
                throw Failure("open PTY slave");
            }

            return (master, slave);
        }
        catch
        {
            Close(slave);
            Close(master);
            throw;
        }
    }

    public static void Resize(int descriptor, ushort rows, ushort columns)
    {
        var size = new WindowSize(rows, columns, 0, 0);

        if (SetDescriptorWindowSize(descriptor, SetWindowSize, in size) != 0)
        {
            throw Failure("resize PTY");
        }
    }

    public static unsafe int Read(int descriptor, byte[] buffer)
    {
        fixed (byte* pointer = buffer)
        {
            while (true)
            {
                var count = ReadDescriptor(descriptor, pointer, (nuint)buffer.Length);

                if (count >= 0)
                {
                    return checked((int)count);
                }

                var error = Marshal.GetLastPInvokeError();

                if (error == InputOutputError)
                {
                    return 0;
                }

                if (error != InterruptedSystemCall)
                {
                    throw Failure("read PTY", error);
                }
            }
        }
    }

    public static unsafe int Write(int descriptor, ReadOnlySpan<byte> bytes)
    {
        fixed (byte* pointer = bytes)
        {
            while (true)
            {
                var count = WriteDescriptor(descriptor, pointer, (nuint)bytes.Length);

                if (count >= 0)
                {
                    return checked((int)count);
                }

                var error = Marshal.GetLastPInvokeError();

                if (error != InterruptedSystemCall)
                {
                    throw Failure("write PTY", error);
                }
            }
        }
    }

    public static void Close(int descriptor)
    {
        if (descriptor >= 0)
        {
            _ = CloseDescriptor(descriptor);
        }
    }

    private static IOException Failure(string operation) => Failure(operation, Marshal.GetLastPInvokeError());

    private static IOException Failure(string operation, int error) =>
        new($"failed to {operation} (errno {error})");

    [LibraryImport("libc", EntryPoint = "posix_openpt", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int OpenPseudoTerminal(int flags);

    [LibraryImport("libc", EntryPoint = "grantpt", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int GrantPseudoTerminal(int descriptor);

    [LibraryImport("libc", EntryPoint = "unlockpt", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int UnlockPseudoTerminal(int descriptor);

    [LibraryImport("libc", EntryPoint = "ptsname_r")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int GetPseudoTerminalName(int descriptor, byte[] buffer, nuint length);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int OpenDescriptor(string path, int flags);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int SetDescriptorWindowSize(int descriptor, nuint request, in WindowSize size);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static unsafe partial nint ReadDescriptor(int descriptor, byte* buffer, nuint length);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static unsafe partial nint WriteDescriptor(int descriptor, byte* buffer, nuint length);

    [LibraryImport("libc", EntryPoint = "close")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int CloseDescriptor(int descriptor);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct WindowSize(ushort Rows, ushort Columns, ushort PixelWidth, ushort PixelHeight);
}
