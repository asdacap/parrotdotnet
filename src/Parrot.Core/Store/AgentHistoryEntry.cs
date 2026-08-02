using System.Text.Json.Serialization;

namespace Parrot.Store;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AgentHistoryMessageEntry), "message")]
[JsonDerivedType(typeof(AgentHistoryCompactionEntry), "compaction")]
internal abstract record AgentHistoryEntry([property: JsonPropertyName("sequence")] long Sequence);
