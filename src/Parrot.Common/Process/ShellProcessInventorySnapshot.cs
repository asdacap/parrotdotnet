namespace Parrot.Process;

internal sealed record ShellProcessInventorySnapshot(
    string OwnerAgentSessionId,
    string InventoryInstanceId,
    ulong Revision,
    bool Removed,
    IReadOnlyList<ActiveShellProcessState> Processes,
    IReadOnlyList<CompletedShellProcessState> CompletedProcesses);
