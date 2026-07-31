using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AgentSendTool.Input), TypeInfoPropertyName = "AgentSendToolInput")]
[JsonSerializable(typeof(AgentSpawnTool.Input), TypeInfoPropertyName = "AgentSpawnToolInput")]
[JsonSerializable(typeof(ExecCommandTool.Input), TypeInfoPropertyName = "ExecCommandToolInput")]
[JsonSerializable(typeof(WaitAgentTool.Input), TypeInfoPropertyName = "WaitAgentToolInput")]
internal sealed partial class AgentProcessToolJsonContext : JsonSerializerContext;
