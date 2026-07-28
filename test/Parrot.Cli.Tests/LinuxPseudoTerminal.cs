using System.Runtime.InteropServices;
using System.Text;
using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed partial class LinuxPseudoTerminal : IDisposable
{
    private const int OpenReadWrite = 0x00000002;
    private const int OpenNoControllingTerminal = 0x00000100;
    private const int SetNow = 0;
    private const short PollInput = 0x0001;
    private readonly int _master;
    private readonly int _slave;
    private bool _disposed;

    private LinuxPseudoTerminal(int master, int slave)
    {
        _master = master;
        _slave = slave;
    }

    public static LinuxPseudoTerminal Open()
    {
        var master = PosixOpenPseudoTerminal(OpenReadWrite | OpenNoControllingTerminal);
        if (master < 0)
        {
            throw NativeFailure("failed to open PTY master");
        }

        var slave = -1;
        try
        {
            EnsureSuccess(GrantPseudoTerminal(master), "failed to grant PTY slave");
            EnsureSuccess(UnlockPseudoTerminal(master), "failed to unlock PTY slave");

            var pathBytes = new byte[256];
            EnsureSuccess(GetPseudoTerminalName(master, pathBytes, (nuint)pathBytes.Length), "failed to name PTY slave");
            var terminator = Array.IndexOf(pathBytes, (byte)0);
            if (terminator < 0)
            {
                throw new IOException("PTY slave path was not null-terminated.");
            }

            var path = Encoding.UTF8.GetString(pathBytes.AsSpan(0, terminator));
            slave = OpenFile(path, OpenReadWrite | OpenNoControllingTerminal);
            if (slave < 0)
            {
                throw NativeFailure("failed to open PTY slave");
            }

            return new LinuxPseudoTerminal(master, slave);
        }
        catch
        {
            if (slave >= 0)
            {
                _ = CloseFile(slave);
            }

            _ = CloseFile(master);
            throw;
        }
    }

    public unsafe LinuxTermios GetSlaveAttributes()
    {
        EnsureNotDisposed();
        var attributes = default(LinuxTermios);
        if (GetAttributes(_slave, (nint)(&attributes)) != 0)
        {
            throw NativeFailure("failed to read PTY slave attributes");
        }

        return attributes;
    }

    public unsafe void SetSlaveAttributes(LinuxTermios attributes)
    {
        EnsureNotDisposed();
        if (SetAttributes(_slave, SetNow, (nint)(&attributes)) != 0)
        {
            throw NativeFailure("failed to set PTY slave attributes");
        }
    }

    public void WriteMaster(byte value)
    {
        EnsureNotDisposed();
        if (WriteFile(_master, [value], 1) != 1)
        {
            throw NativeFailure("failed to write PTY master");
        }
    }

    public byte ReadSlave()
    {
        EnsureNotDisposed();
        if (!HasInput(_slave, 500))
        {
            throw new IOException("Timed out waiting for PTY slave input.");
        }

        var buffer = new byte[1];
        if (ReadFile(_slave, buffer, 1) != 1)
        {
            throw NativeFailure("failed to read PTY slave");
        }

        return buffer[0];
    }

    public bool MasterHasOutput(int timeoutMilliseconds)
    {
        EnsureNotDisposed();
        return HasInput(_master, timeoutMilliseconds);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = CloseFile(_slave);
        _ = CloseFile(_master);
        GC.SuppressFinalize(this);
    }

    private static bool HasInput(int descriptor, int timeoutMilliseconds)
    {
        var pollDescriptor = new PollDescriptor { Descriptor = descriptor, Events = PollInput };
        var result = Poll(ref pollDescriptor, 1, timeoutMilliseconds);
        if (result < 0)
        {
            throw NativeFailure("failed to poll PTY descriptor");
        }

        return result > 0 && (pollDescriptor.ReturnedEvents & PollInput) != 0;
    }

    private static void EnsureSuccess(int result, string message)
    {
        if (result != 0)
        {
            throw NativeFailure(message);
        }
    }

    private static IOException NativeFailure(string message) =>
        new($"{message} (errno {Marshal.GetLastPInvokeError()}).");

    [LibraryImport("libc", EntryPoint = "posix_openpt", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int PosixOpenPseudoTerminal(int flags);

    [LibraryImport("libc", EntryPoint = "grantpt", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int GrantPseudoTerminal(int descriptor);

    [LibraryImport("libc", EntryPoint = "unlockpt", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int UnlockPseudoTerminal(int descriptor);

    [LibraryImport("libc", EntryPoint = "ptsname_r", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int GetPseudoTerminalName(int descriptor, byte[] buffer, nuint length);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int OpenFile(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int CloseFile(int descriptor);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial nint ReadFile(int descriptor, byte[] buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial nint WriteFile(int descriptor, byte[] buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Poll(ref PollDescriptor descriptor, nuint count, int timeoutMilliseconds);

    [LibraryImport("libc", EntryPoint = "tcgetattr", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int GetAttributes(int descriptor, nint attributes);

    [LibraryImport("libc", EntryPoint = "tcsetattr", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int SetAttributes(int descriptor, int actions, nint attributes);

    private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int Descriptor;
        public short Events;
        public short ReturnedEvents;
    }
}
