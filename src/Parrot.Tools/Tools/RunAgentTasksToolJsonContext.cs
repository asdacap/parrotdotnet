using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(RunAgentTasksTool.Input), TypeInfoPropertyName = "RunAgentTasksToolInput")]
internal sealed partial class RunAgentTasksToolJsonContext : JsonSerializerContext;
