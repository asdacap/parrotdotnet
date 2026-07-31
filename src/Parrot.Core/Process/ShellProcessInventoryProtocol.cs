using Parrot.Protocol;
using ProtocolActiveShellProcess = Parrot.Protocol.ActiveShellProcess;

namespace Parrot.Process;

internal static class ShellProcessInventoryProtocol
{
    private const int MaximumEventBytes = 32 * 1024 * 1024;

    public static IEnumerable<Event> Convert(ShellProcessInventorySnapshot inventory)
    {
        var chunkCount = checked((uint)Math.Max(1, inventory.Processes.Count));
        if (inventory.Processes.Count == 0)
        {
            yield return Build(inventory, 0, chunkCount, null);
            yield break;
        }

        for (var index = 0; index < inventory.Processes.Count; index++)
        {
            yield return Build(inventory, checked((uint)index), chunkCount, inventory.Processes[index]);
        }
    }

    private static Event Build(
        ShellProcessInventorySnapshot inventory,
        uint chunkIndex,
        uint chunkCount,
        ActiveShellProcessState? state)
    {
        var snapshot = new ShellProcessSnapshot
        {
            InventoryInstanceId = inventory.InventoryInstanceId,
            Revision = inventory.Revision,
            ChunkIndex = chunkIndex,
            ChunkCount = chunkCount,
        };
        if (state is not null)
        {
            snapshot.Processes.Add(new ProtocolActiveShellProcess
            {
                ProcessId = state.ProcessId,
                Name = state.Name,
                Command = state.Command,
                OriginToolCallId = state.OriginToolCallId,
                OwnerAgentSessionId = state.OwnerAgentSessionId,
                OwnerAgentName = state.OwnerAgentName,
                ParentAgentSessionId = state.ParentAgentSessionId,
                ParentAgentName = state.ParentAgentName,
                Depth = state.Depth,
                ElapsedMs = state.ElapsedMilliseconds,
            });
        }

        var published = new Event { ShellProcessSnapshot = snapshot };
        if (published.CalculateSize() > MaximumEventBytes)
        {
            throw new InvalidOperationException($"Shell process inventory record exceeds {MaximumEventBytes} bytes.");
        }

        return published;
    }
}
