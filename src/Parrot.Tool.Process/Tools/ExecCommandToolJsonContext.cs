using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ExecCommandTool.Input), TypeInfoPropertyName = "ExecCommandToolInput")]
internal sealed partial class ExecCommandToolJsonContext : JsonSerializerContext;
