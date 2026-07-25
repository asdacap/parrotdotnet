using System.Runtime.InteropServices;

namespace Parrot.Cli.Enhanced;

internal sealed partial class UnixRawTerminal : IRawTerminal
{
    private const int StandardInput = 0;
    private const int SetNow = 0;

    private const uint LinuxCharacterSize = 0x00000030U;
    private const uint LinuxEightBits = 0x00000030U;
    private const uint LinuxRawInputFlags = 0x00000532U;
    private const uint LinuxRawLocalFlags = 0x0000800bU;
    private const ulong DarwinCharacterSize = 0x00000300UL;
    private const ulong DarwinEightBits = 0x00000300UL;
    private const ulong DarwinRawInputFlags = 0x00000332UL;
    private const ulong DarwinRawLocalFlags = 0x00000588UL;

    private readonly Action _ensureRaw;
    private readonly Stream _input;
    private readonly Action _restore;
    private bool _disposed;

    private UnixRawTerminal(Stream input, Action ensureRaw, Action restore)
    {
        _input = input;
        _ensureRaw = ensureRaw;
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

    public ValueTask<int> Read(byte[] buffer, CancellationToken cancellationToken)
    {
        _ensureRaw();
        return _input.ReadAsync(buffer.AsMemory(), cancellationToken);
    }

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

    internal static unsafe LinuxTermios MakeLinuxRaw(LinuxTermios original)
    {
        var raw = original;
        raw.InputFlags &= ~LinuxRawInputFlags;
        raw.ControlFlags = (raw.ControlFlags & ~LinuxCharacterSize) | LinuxEightBits;
        raw.LocalFlags &= ~LinuxRawLocalFlags;
        raw.ControlCharacters[6] = 0;
        raw.ControlCharacters[5] = 1;
        return raw;
    }

    internal static unsafe DarwinTermios MakeDarwinRaw(DarwinTermios original)
    {
        var raw = original;
        raw.InputFlags &= ~DarwinRawInputFlags;
        raw.ControlFlags = (raw.ControlFlags & ~DarwinCharacterSize) | DarwinEightBits;
        raw.LocalFlags &= ~DarwinRawLocalFlags;
        raw.ControlCharacters[16] = 0;
        raw.ControlCharacters[17] = 1;
        return raw;
    }

    [LibraryImport("libc", EntryPoint = "isatty", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int IsTerminal(int descriptor);

    private static unsafe UnixRawTerminal? OpenLinux()
    {
        var input = Console.OpenStandardInput();
        if (GetLinuxAttributes(StandardInput, out var original) != 0)
        {
            input.Dispose();
            return null;
        }

        var raw = MakeLinuxRaw(original);
        if (!TrySetLinuxRaw(raw))
        {
            RestoreLinux(original);
            input.Dispose();
            return null;
        }

        return new UnixRawTerminal(
            input,
            () => EnsureLinuxRaw(raw),
            () => RestoreLinux(original));
    }

    private static unsafe UnixRawTerminal? OpenDarwin()
    {
        var input = Console.OpenStandardInput();
        if (GetDarwinAttributes(StandardInput, out var original) != 0)
        {
            input.Dispose();
            return null;
        }

        var raw = MakeDarwinRaw(original);
        if (!TrySetDarwinRaw(raw))
        {
            RestoreDarwin(original);
            input.Dispose();
            return null;
        }

        return new UnixRawTerminal(
            input,
            () => EnsureDarwinRaw(raw),
            () => RestoreDarwin(original));
    }

    private static void EnsureLinuxRaw(LinuxTermios raw)
    {
        if (!TrySetLinuxRaw(raw))
        {
            throw new IOException($"failed to enable raw terminal mode (errno {Marshal.GetLastPInvokeError()})");
        }
    }

    private static void EnsureDarwinRaw(DarwinTermios raw)
    {
        if (!TrySetDarwinRaw(raw))
        {
            throw new IOException($"failed to enable raw terminal mode (errno {Marshal.GetLastPInvokeError()})");
        }
    }

    private static bool TrySetLinuxRaw(LinuxTermios raw) =>
        SetLinuxAttributes(StandardInput, SetNow, in raw) == 0
        && GetLinuxAttributes(StandardInput, out var applied) == 0
        && (applied.LocalFlags & LinuxRawLocalFlags) == 0;

    private static bool TrySetDarwinRaw(DarwinTermios raw) =>
        SetDarwinAttributes(StandardInput, SetNow, in raw) == 0
        && GetDarwinAttributes(StandardInput, out var applied) == 0
        && (applied.LocalFlags & DarwinRawLocalFlags) == 0;

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
