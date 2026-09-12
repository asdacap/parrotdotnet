namespace Parrot.Store;

internal sealed class RuntimeIdentity(
    string hostIdentity,
    string bootIdentity,
    int processId,
    string processStartToken,
    string runtimeInstanceId,
    Func<int, ProcessIdentity> inspectProcess)
{
    public string HostIdentity { get; } = hostIdentity;

    public string BootIdentity { get; } = bootIdentity;

    public int ProcessId { get; } = processId;

    public string ProcessStartToken { get; } = processStartToken;

    public string RuntimeInstanceId { get; } = runtimeInstanceId;

    public Func<int, ProcessIdentity> Inspect { get; } = inspectProcess;
}
