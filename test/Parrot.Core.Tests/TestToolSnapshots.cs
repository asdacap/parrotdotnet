using Google.Protobuf;
using Parrot.AgentTasks;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.Queues;

namespace Parrot.Core.Tests;

// Builds and reads ToolSnapshot events: a test states the envelope and the data, and this fills in the kind.
internal static class TestToolSnapshots
{
    public static Event Queue(ToolSnapshot inventory, QueueSnapshot data) => new() { ToolSnapshot = QueueEnvelope(inventory, data) };

    public static Event Process(ToolSnapshot inventory, ShellProcessSnapshot data) => new() { ToolSnapshot = ProcessEnvelope(inventory, data) };

    public static ToolSnapshot QueueEnvelope(ToolSnapshot inventory, QueueSnapshot data) =>
        Inventory(inventory, QueueInventoryProtocol.Kind, data);

    public static ToolSnapshot ProcessEnvelope(ToolSnapshot inventory, ShellProcessSnapshot data) =>
        Inventory(inventory, ShellProcessInventoryProtocol.Kind, data);

    public static Event Progress(string agentSessionId, AgentTaskProgressSnapshot data) =>
        new() { AgentSessionId = agentSessionId, ToolSnapshot = ProgressInventory(data) };

    public static ToolSnapshot ProgressInventory(AgentTaskProgressSnapshot data) =>
        Inventory(new ToolSnapshot { Revision = data.Revision, FinalChunk = true }, AgentTaskService.ProgressKind, data);

    public static ToolSnapshot? QueueInventory(Event published) =>
        published.ToolSnapshot?.Kind == QueueInventoryProtocol.Kind ? published.ToolSnapshot : null;

    public static ToolSnapshot? ProcessInventory(Event published) =>
        published.ToolSnapshot?.Kind == ShellProcessInventoryProtocol.Kind ? published.ToolSnapshot : null;

    public static QueueSnapshot QueueData(Event published) =>
        QueueSnapshot.Parser.ParseFrom(Data(published, QueueInventoryProtocol.Kind));

    public static ShellProcessSnapshot ProcessData(Event published) =>
        ShellProcessSnapshot.Parser.ParseFrom(Data(published, ShellProcessInventoryProtocol.Kind));

    public static AgentTaskProgressSnapshot ProgressData(Event published) =>
        AgentTaskProgressSnapshot.Parser.ParseFrom(Data(published, AgentTaskService.ProgressKind));

    private static ByteString Data(Event published, string kind) =>
        published.ToolSnapshot?.Kind == kind
            ? published.ToolSnapshot.Data
            : throw new InvalidOperationException($"Expected a {kind} tool snapshot.");

    private static ToolSnapshot Inventory(ToolSnapshot inventory, string kind, IMessage data)
    {
        inventory.Kind = kind;
        inventory.Data = data.ToByteString();
        return inventory;
    }
}
