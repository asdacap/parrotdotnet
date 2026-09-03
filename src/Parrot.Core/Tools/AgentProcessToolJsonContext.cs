using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AgentSendTool.Input), TypeInfoPropertyName = "AgentSendToolInput")]
[JsonSerializable(typeof(AgentStatusTool.Input), TypeInfoPropertyName = "AgentStatusToolInput")]
[JsonSerializable(typeof(AgentSpawnTool.Input), TypeInfoPropertyName = "AgentSpawnToolInput")]
[JsonSerializable(typeof(SetCheckpointTool.Input), TypeInfoPropertyName = "SetCheckpointToolInput")]
[JsonSerializable(typeof(SetExitReminderTool.Input), TypeInfoPropertyName = "SetExitReminderToolInput")]
[JsonSerializable(typeof(ExecCommandTool.Input), TypeInfoPropertyName = "ExecCommandToolInput")]
internal sealed partial class AgentProcessToolJsonContext : JsonSerializerContext;
