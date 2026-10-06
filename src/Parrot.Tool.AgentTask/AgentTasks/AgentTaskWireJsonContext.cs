using System.Text.Json.Serialization;

namespace Parrot.AgentTasks;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AgentTaskArtifactWire), TypeInfoPropertyName = "AgentTaskArtifactWire")]
[JsonSerializable(typeof(AgentTaskWire[]), TypeInfoPropertyName = "AgentTaskWireArray")]
[JsonSerializable(typeof(AgentTaskLeafResponseWire), TypeInfoPropertyName = "AgentTaskLeafResponseWire")]
internal sealed partial class AgentTaskWireJsonContext : JsonSerializerContext;
