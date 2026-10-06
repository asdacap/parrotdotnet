using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SetAgentTasksTool.Input), TypeInfoPropertyName = "SetAgentTasksToolInput")]
[JsonSerializable(typeof(GetAgentTasksTool.Input), TypeInfoPropertyName = "GetAgentTasksToolInput")]
internal sealed partial class AgentTasksToolJsonContext : JsonSerializerContext;
