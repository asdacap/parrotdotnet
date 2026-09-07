using Parrot.Protocol;
using ProtocolActiveShellProcess = Parrot.Protocol.ActiveShellProcess;
using ProtocolCompletedShellProcess = Parrot.Protocol.CompletedShellProcess;

namespace Parrot.Process;

internal static class ShellProcessInventoryProtocol
{
    private const int MaximumEventBytes = 32 * 1024 * 1024;

    public static IEnumerable<Event> Convert(ShellProcessInventorySnapshot inventory)
    {
        var recordCount = checked(inventory.Processes.Count + inventory.CompletedProcesses.Count);
        var chunkCount = checked((uint)Math.Max(1, recordCount));
        if (recordCount == 0)
        {
            yield return BuildEmpty(inventory, chunkCount);
            yield break;
        }

        var chunkIndex = 0U;
        foreach (var process in inventory.Processes)
        {
            yield return BuildActive(inventory, chunkIndex++, chunkCount, process);
        }

        foreach (var process in inventory.CompletedProcesses)
        {
            yield return BuildCompleted(inventory, chunkIndex++, chunkCount, process);
        }
    }

    private static Event BuildEmpty(ShellProcessInventorySnapshot inventory, uint chunkCount) =>
        Validate(new Event
        {
            ShellProcessSnapshot = new ShellProcessSnapshot
            {
                InventoryInstanceId = inventory.InventoryInstanceId,
                Revision = inventory.Revision,
                ChunkIndex = 0,
                ChunkCount = chunkCount,
            },
        });

    private static Event BuildActive(
        ShellProcessInventorySnapshot inventory,
        uint chunkIndex,
        uint chunkCount,
        ActiveShellProcessState state)
    {
        var snapshot = new ShellProcessSnapshot
        {
            InventoryInstanceId = inventory.InventoryInstanceId,
            Revision = inventory.Revision,
            ChunkIndex = chunkIndex,
            ChunkCount = chunkCount,
        };
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
        return Validate(new Event { ShellProcessSnapshot = snapshot });
    }

    private static Event BuildCompleted(
        ShellProcessInventorySnapshot inventory,
        uint chunkIndex,
        uint chunkCount,
        CompletedShellProcessState state)
    {
        var completed = new ProtocolCompletedShellProcess { ProcessId = state.ProcessId };
        if (state.ElapsedMilliseconds is { } elapsedMilliseconds)
        {
            completed.ElapsedMs = elapsedMilliseconds;
        }

        var snapshot = new ShellProcessSnapshot
        {
            InventoryInstanceId = inventory.InventoryInstanceId,
            Revision = inventory.Revision,
            ChunkIndex = chunkIndex,
            ChunkCount = chunkCount,
        };
        snapshot.CompletedProcesses.Add(completed);
        return Validate(new Event { ShellProcessSnapshot = snapshot });
    }

    private static Event Validate(Event published)
    {
        if (published.CalculateSize() > MaximumEventBytes)
        {
            throw new InvalidOperationException($"Shell process inventory record exceeds {MaximumEventBytes} bytes.");
        }

        return published;
    }
}
