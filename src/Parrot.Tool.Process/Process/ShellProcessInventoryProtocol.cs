using Google.Protobuf;
using Parrot.Protocol;
using ProtocolActiveShellProcess = Parrot.Protocol.ActiveShellProcess;
using ProtocolCompletedShellProcess = Parrot.Protocol.CompletedShellProcess;

namespace Parrot.Process;

internal static class ShellProcessInventoryProtocol
{
    internal const string Kind = "shell_process";
    private const int MaximumEventBytes = 32 * 1024 * 1024;

    public static IEnumerable<Event> Convert(ShellProcessInventorySnapshot inventory)
    {
        var recordCount = checked(inventory.Processes.Count + inventory.CompletedProcesses.Count);
        if (recordCount == 0)
        {
            yield return Build(inventory, 0, true, new ShellProcessSnapshot());
            yield break;
        }

        var chunkIndex = 0U;
        foreach (var process in inventory.Processes)
        {
            var snapshot = new ShellProcessSnapshot();
            snapshot.Processes.Add(Active(process));
            yield return Build(inventory, chunkIndex, ++chunkIndex == recordCount, snapshot);
        }

        foreach (var process in inventory.CompletedProcesses)
        {
            var snapshot = new ShellProcessSnapshot();
            snapshot.CompletedProcesses.Add(Completed(process));
            yield return Build(inventory, chunkIndex, ++chunkIndex == recordCount, snapshot);
        }
    }

    private static ProtocolActiveShellProcess Active(ActiveShellProcessState state) => new()
    {
        ProcessId = state.ProcessId,
        Name = state.Name,
        Command = state.Command,
        Description = state.Description,
        OriginToolCallId = state.OriginToolCallId,
        OwnerAgentSessionId = state.OwnerAgentSessionId,
        OwnerAgentName = state.OwnerAgentName,
        ParentAgentSessionId = state.ParentAgentSessionId,
        ParentAgentName = state.ParentAgentName,
        Depth = state.Depth,
        ElapsedMs = state.ElapsedMilliseconds,
        ActivityKind = state.ActivityKind,
    };

    private static ProtocolCompletedShellProcess Completed(CompletedShellProcessState state)
    {
        var completed = new ProtocolCompletedShellProcess { ProcessId = state.ProcessId };
        if (state.ElapsedMilliseconds is { } elapsedMilliseconds)
        {
            completed.ElapsedMs = elapsedMilliseconds;
        }

        return completed;
    }

    private static Event Build(
        ShellProcessInventorySnapshot inventory,
        uint chunkIndex,
        bool finalChunk,
        ShellProcessSnapshot snapshot)
    {
        var published = new Event
        {
            ToolSnapshot = new ToolSnapshot
            {
                Kind = Kind,
                OwnerAgentSessionId = inventory.OwnerAgentSessionId,
                Removed = inventory.Removed,
                InventoryInstanceId = inventory.InventoryInstanceId,
                Revision = inventory.Revision,
                ChunkIndex = chunkIndex,
                FinalChunk = finalChunk,
                Data = snapshot.ToByteString(),
            },
        };
        if (published.CalculateSize() > MaximumEventBytes)
        {
            throw new InvalidOperationException($"Shell process inventory record exceeds {MaximumEventBytes} bytes.");
        }

        return published;
    }
}
