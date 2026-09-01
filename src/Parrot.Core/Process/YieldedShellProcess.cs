namespace Parrot.Process;

internal sealed record YieldedShellProcess(
    string ProcessId,
    string Name,
    string InventoryInstanceId,
    ulong VisibleRevision,
    string? StdoutPath,
    string? StderrPath);
