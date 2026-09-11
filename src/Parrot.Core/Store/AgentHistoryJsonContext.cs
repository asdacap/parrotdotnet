using System.Text.Json.Serialization;

namespace Parrot.Store;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AgentHistoryEntry))]
[JsonSerializable(typeof(AgentHistoryMessageEntry))]
[JsonSerializable(typeof(AgentHistoryCompactionEntry))]
[JsonSerializable(typeof(AgentHistoryRequestEntry))]
[JsonSerializable(typeof(AgentHistoryPart))]
[JsonSerializable(typeof(AgentHistoryToolCall))]
internal sealed partial class AgentHistoryJsonContext : JsonSerializerContext;
