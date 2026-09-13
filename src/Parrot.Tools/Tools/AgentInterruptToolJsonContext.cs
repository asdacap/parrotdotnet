using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AgentInterruptTool.Input), TypeInfoPropertyName = "AgentInterruptToolInput")]
internal sealed partial class AgentInterruptToolJsonContext : JsonSerializerContext;
