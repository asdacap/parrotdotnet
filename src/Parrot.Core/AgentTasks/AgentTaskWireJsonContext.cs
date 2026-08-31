using System.Text.Json.Serialization;

namespace Parrot.AgentTasks;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AgentTaskArtifactWire), TypeInfoPropertyName = "AgentTaskArtifactWire")]
[JsonSerializable(typeof(ResearchHookWire), TypeInfoPropertyName = "ResearchHookWire")]
[JsonSerializable(typeof(AcceptanceVerdictWire), TypeInfoPropertyName = "AcceptanceVerdictWire")]
[JsonSerializable(typeof(AgentTaskResultWire), TypeInfoPropertyName = "AgentTaskResultWire")]
internal sealed partial class AgentTaskWireJsonContext : JsonSerializerContext;
