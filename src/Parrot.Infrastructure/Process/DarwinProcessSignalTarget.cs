using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Parrot.Process;

internal sealed partial class DarwinProcessSignalTarget : IProcessSignalTarget
{
    private const int InvalidArgument = 22;
    private const int NoSuchProcess = 3;
    private const int PermissionDenied = 1;
    private readonly int _processId;

    private DarwinProcessSignalTarget(int processId) => _processId = processId;

    public static IProcessSignalTarget Open(System.Diagnostics.Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Darwin process signals require macOS.");
        }

        return new DarwinProcessSignalTarget(process.Id);
    }

    public void Send(ProcessSignal signal)
    {
        if (Kill(_processId, signal.Value) == 0)
        {
            return;
        }

        throw Failure(Marshal.GetLastPInvokeError(), signal.Value);
    }

    public void Dispose()
    {
    }

    private static Exception Failure(int error, int signal) => error switch
    {
        NoSuchProcess => new InvalidOperationException("The shell process has completed."),
        InvalidArgument => new InvalidOperationException($"Process signal {signal} is invalid on this host."),
        PermissionDenied => new InvalidOperationException("Permission was denied while signaling the shell process."),
        _ => new IOException($"Failed to send the signal: {new Win32Exception(error).Message}"),
    };

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int processId, int signal);
}
