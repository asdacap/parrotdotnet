namespace Parrot.Process;

internal sealed record ShellProcessInventorySnapshot(
    string InventoryInstanceId,
    ulong Revision,
    IReadOnlyList<ActiveShellProcessState> Processes);
