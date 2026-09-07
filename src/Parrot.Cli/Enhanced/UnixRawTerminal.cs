using System.Runtime.InteropServices;

namespace Parrot.Cli.Enhanced;

internal sealed partial class UnixRawTerminal : IRawTerminal
{
    private const int StandardInput = 0;
    private const int SetNow = 0;
    private const int InterruptedSystemCall = 4;
    private const int LinuxResourceTemporarilyUnavailable = 11;
    private const int DarwinResourceTemporarilyUnavailable = 35;
    private const int MaximumReadRetries = 5;
    private const int InitialReadRetryDelayMilliseconds = 10;

    private const uint LinuxCharacterSize = 0x00000030U;
    private const uint LinuxEightBits = 0x00000030U;
    private const uint LinuxRawInputFlags = 0x00000532U;
    private const uint LinuxRawLocalFlags = 0x0000800bU;
    private const ulong DarwinCharacterSize = 0x00000300UL;
    private const ulong DarwinEightBits = 0x00000300UL;
    private const ulong DarwinRawInputFlags = 0x00000332UL;
    private const ulong DarwinRawLocalFlags = 0x00000588UL;

    private readonly int _descriptor;
    private readonly Action _ensureRaw;
    private readonly Action _restore;
    private bool _disposed;

    private UnixRawTerminal(int descriptor, Action ensureRaw, Action restore)
    {
        _descriptor = descriptor;
        _ensureRaw = ensureRaw;
        _restore = restore;
    }

    public static UnixRawTerminal? Open() =>
        string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.Ordinal)
            ? null
            : Open(StandardInput);

    public ValueTask<int> Read(byte[] buffer, CancellationToken cancellationToken)
    {
        _ensureRaw();
        return new(Task.Run(() => ReadInput(_descriptor, buffer, cancellationToken), cancellationToken));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _restore();
    }

    internal static UnixRawTerminal? Open(int descriptor)
    {
        if (IsTerminal(descriptor) != 1)
        {
            return null;
        }

        return OperatingSystem.IsLinux()
            ? OpenLinux(descriptor)
            : OperatingSystem.IsMacOS() ? OpenDarwin(descriptor) : null;
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

    internal static int ReadInputWithRetry(
        Func<(nint Count, int Error)> read,
        Action<TimeSpan> delay,
        int resourceTemporarilyUnavailable)
    {
        var retryCount = 0;
        while (true)
        {
            var (count, error) = read();
            if (count >= 0)
            {
                return (int)count;
            }

            if (error == InterruptedSystemCall)
            {
                continue;
            }

            if (error != resourceTemporarilyUnavailable || retryCount >= MaximumReadRetries)
            {
                throw new IOException($"failed to read terminal input (errno {error})");
            }

            delay(TimeSpan.FromMilliseconds(InitialReadRetryDelayMilliseconds << retryCount));
            retryCount++;
        }
    }

    [LibraryImport("libc", EntryPoint = "isatty", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int IsTerminal(int descriptor);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial nint ReadDescriptor(int descriptor, byte[] buffer, nuint count);

    // Not Console.OpenStandardInput: on a terminal that stream is a UnixConsoleStream with
    // useReadLine, so every read goes through StdInReader, which re-implements canonical mode
    // in managed code and software-echoes each keystroke to stdout in the default colours,
    // whatever termios says.
    //
    // MakeLinuxRaw and MakeDarwinRaw set VMIN=0/VTIME=1, so read(2) returns zero bytes after
    // 100ms of silence. That zero is a poll tick -- EnhancedCli flushes a pending lone ESC and
    // rechecks cancellation on it -- and never end of input, so nothing may latch it.
    private static int ReadInput(int descriptor, byte[] buffer, CancellationToken cancellationToken)
    {
        var resourceTemporarilyUnavailable = OperatingSystem.IsMacOS()
            ? DarwinResourceTemporarilyUnavailable
            : LinuxResourceTemporarilyUnavailable;
        return ReadInputWithRetry(
            () =>
            {
                var count = ReadDescriptor(descriptor, buffer, (nuint)buffer.Length);
                return (count, count < 0 ? Marshal.GetLastPInvokeError() : 0);
            },
            delay =>
            {
                if (cancellationToken.WaitHandle.WaitOne(delay))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            },
            resourceTemporarilyUnavailable);
    }

    private static unsafe UnixRawTerminal? OpenLinux(int descriptor)
    {
        if (GetLinuxAttributes(descriptor, out var original) != 0)
        {
            return null;
        }

        var raw = MakeLinuxRaw(original);
        if (!TrySetLinuxRaw(descriptor, raw))
        {
            RestoreLinux(descriptor, original);
            return null;
        }

        return new UnixRawTerminal(
            descriptor,
            () => EnsureLinuxRaw(descriptor, raw),
            () => RestoreLinux(descriptor, original));
    }

    private static unsafe UnixRawTerminal? OpenDarwin(int descriptor)
    {
        if (GetDarwinAttributes(descriptor, out var original) != 0)
        {
            return null;
        }

        var raw = MakeDarwinRaw(original);
        if (!TrySetDarwinRaw(descriptor, raw))
        {
            RestoreDarwin(descriptor, original);
            return null;
        }

        return new UnixRawTerminal(
            descriptor,
            () => EnsureDarwinRaw(descriptor, raw),
            () => RestoreDarwin(descriptor, original));
    }

    private static void EnsureLinuxRaw(int descriptor, LinuxTermios raw)
    {
        if (!TrySetLinuxRaw(descriptor, raw))
        {
            throw new IOException($"failed to enable raw terminal mode (errno {Marshal.GetLastPInvokeError()})");
        }
    }

    private static void EnsureDarwinRaw(int descriptor, DarwinTermios raw)
    {
        if (!TrySetDarwinRaw(descriptor, raw))
        {
            throw new IOException($"failed to enable raw terminal mode (errno {Marshal.GetLastPInvokeError()})");
        }
    }

    private static bool TrySetLinuxRaw(int descriptor, LinuxTermios raw) =>
        SetLinuxAttributes(descriptor, SetNow, in raw) == 0
        && GetLinuxAttributes(descriptor, out var applied) == 0
        && (applied.LocalFlags & LinuxRawLocalFlags) == 0;

    private static bool TrySetDarwinRaw(int descriptor, DarwinTermios raw) =>
        SetDarwinAttributes(descriptor, SetNow, in raw) == 0
        && GetDarwinAttributes(descriptor, out var applied) == 0
        && (applied.LocalFlags & DarwinRawLocalFlags) == 0;

    private static void RestoreLinux(int descriptor, LinuxTermios original)
    {
        if (SetLinuxAttributes(descriptor, SetNow, in original) != 0)
        {
            throw new IOException($"failed to restore terminal attributes (errno {Marshal.GetLastPInvokeError()})");
        }
    }

    private static void RestoreDarwin(int descriptor, DarwinTermios original)
    {
        if (SetDarwinAttributes(descriptor, SetNow, in original) != 0)
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
