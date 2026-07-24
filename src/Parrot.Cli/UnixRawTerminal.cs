using System.Runtime.InteropServices;

namespace Parrot.Cli;

internal sealed partial class UnixRawTerminal : IRawTerminal
{
    private const int StandardInput = 0;
    private const int SetNow = 0;

    private readonly Stream _input;
    private readonly Action _restore;
    private bool _disposed;

    private UnixRawTerminal(Stream input, Action restore)
    {
        _input = input;
        _restore = restore;
    }

    public static UnixRawTerminal? Open()
    {
        if (IsTerminal(StandardInput) != 1)
        {
            return null;
        }

        return OperatingSystem.IsLinux()
            ? OpenLinux()
            : OperatingSystem.IsMacOS() ? OpenDarwin() : null;
    }

    public ValueTask<int> Read(byte[] buffer, CancellationToken cancellationToken) =>
        _input.ReadAsync(buffer.AsMemory(), cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _restore();
        }
        finally
        {
            _input.Dispose();
        }
    }

    [LibraryImport("libc", EntryPoint = "isatty", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int IsTerminal(int descriptor);

    private static unsafe UnixRawTerminal? OpenLinux()
    {
        if (GetLinuxAttributes(StandardInput, out var original) != 0)
        {
            return null;
        }

        var raw = original;
        raw.InputFlags &= ~0x00000532U;
        raw.ControlFlags = (raw.ControlFlags & ~0x00000030U) | 0x00000030U;
        raw.LocalFlags &= ~0x0000800bU;
        raw.ControlCharacters[6] = 0;
        raw.ControlCharacters[5] = 1;
        if (SetLinuxAttributes(StandardInput, SetNow, in raw) != 0)
        {
            RestoreLinux(original);
            return null;
        }

        return new UnixRawTerminal(Console.OpenStandardInput(), () => RestoreLinux(original));
    }

    private static unsafe UnixRawTerminal? OpenDarwin()
    {
        if (GetDarwinAttributes(StandardInput, out var original) != 0)
        {
            return null;
        }

        var raw = original;
        raw.InputFlags &= ~0x00000332UL;
        raw.ControlFlags = (raw.ControlFlags & ~0x00000300UL) | 0x00000300UL;
        raw.LocalFlags &= ~0x00000588UL;
        raw.ControlCharacters[16] = 0;
        raw.ControlCharacters[17] = 1;
        if (SetDarwinAttributes(StandardInput, SetNow, in raw) != 0)
        {
            RestoreDarwin(original);
            return null;
        }

        return new UnixRawTerminal(Console.OpenStandardInput(), () => RestoreDarwin(original));
    }

    private static void RestoreLinux(LinuxTermios original)
    {
        if (SetLinuxAttributes(StandardInput, SetNow, in original) != 0)
        {
            throw new IOException($"failed to restore terminal attributes (errno {Marshal.GetLastPInvokeError()})");
        }
    }

    private static void RestoreDarwin(DarwinTermios original)
    {
        if (SetDarwinAttributes(StandardInput, SetNow, in original) != 0)
        {
            throw new IOException($"failed to restore terminal attributes (errno {Marshal.GetLastPInvokeError()})");
        }
    }

    [LibraryImport("libc", EntryPoint = "tcgetattr", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int GetLinuxAttributes(int descriptor, out LinuxTermios attributes);

    [LibraryImport("libc", EntryPoint = "tcsetattr", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int SetLinuxAttributes(int descriptor, int actions, in LinuxTermios attributes);

    [LibraryImport("libc", EntryPoint = "tcgetattr", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int GetDarwinAttributes(int descriptor, out DarwinTermios attributes);

    [LibraryImport("libc", EntryPoint = "tcsetattr", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int SetDarwinAttributes(int descriptor, int actions, in DarwinTermios attributes);
}
