using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SetAgentTasksTool.Input), TypeInfoPropertyName = "SetAgentTasksToolInput")]
internal sealed partial class SetAgentTasksToolJsonContext : JsonSerializerContext;
