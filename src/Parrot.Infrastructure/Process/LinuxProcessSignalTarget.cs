using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Parrot.Process;

internal sealed partial class LinuxProcessSignalTarget : IProcessSignalTarget
{
    private const long PidFileDescriptorOpenSystemCall = 434;
    private const long PidFileDescriptorSendSignalSystemCall = 424;
    private const int InvalidArgument = 22;
    private const int NoSuchProcess = 3;
    private const int NotImplemented = 38;
    private const int PermissionDenied = 1;
    private readonly SafeFileHandle? _descriptor;
    private readonly int _openError;

    private LinuxProcessSignalTarget(SafeFileHandle descriptor) => _descriptor = descriptor;

    private LinuxProcessSignalTarget(int openError) => _openError = openError;

    public static IProcessSignalTarget Open(System.Diagnostics.Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Process signals require Linux.");
        }

        var descriptor = OpenPidFileDescriptor(PidFileDescriptorOpenSystemCall, process.Id, 0);
        if (descriptor < 0)
        {
            return new LinuxProcessSignalTarget(Marshal.GetLastPInvokeError());
        }

        return new LinuxProcessSignalTarget(new SafeFileHandle((nint)descriptor, ownsHandle: true));
    }

    public void Send(ProcessSignal signal)
    {
        if (_descriptor is null)
        {
            throw Failure("open a process signal target", _openError, 0);
        }

        try
        {
            if (SendPidFileDescriptorSignal(
                PidFileDescriptorSendSignalSystemCall,
                _descriptor,
                signal.Value,
                0,
                0) != 0)
            {
                throw Failure("send the signal", Marshal.GetLastPInvokeError(), signal.Value);
            }
        }
        catch (ObjectDisposedException failure)
        {
            throw new InvalidOperationException("The shell process signal target is closed.", failure);
        }
    }

    public void Dispose() => _descriptor?.Dispose();

    private static Exception Failure(string operation, int error, int signal) => error switch
    {
        NoSuchProcess => new InvalidOperationException("The shell process has completed."),
        InvalidArgument when signal != 0 =>
            new InvalidOperationException($"Process signal {signal} is invalid on this host."),
        PermissionDenied => new InvalidOperationException("Permission was denied while signaling the shell process."),
        NotImplemented => new PlatformNotSupportedException("This Linux kernel does not support pidfd process signals."),
        _ => new IOException($"Failed to {operation}: {new Win32Exception(error).Message}"),
    };

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long OpenPidFileDescriptor(long number, int processId, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.SafeDirectories)]
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long SendPidFileDescriptorSignal(
        long number,
        SafeFileHandle descriptor,
        int signal,
        nint information,
        uint flags);
}
