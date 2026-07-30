using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AgentSendToolInput))]
[JsonSerializable(typeof(AgentSpawnToolInput))]
[JsonSerializable(typeof(ExecCommandToolInput))]
[JsonSerializable(typeof(WaitAgentToolInput))]
internal sealed partial class AgentProcessToolJsonContext : JsonSerializerContext;
