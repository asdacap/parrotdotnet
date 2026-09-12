using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(StatusTool.Input), TypeInfoPropertyName = "StatusToolInput")]
internal sealed partial class StatusToolJsonContext : JsonSerializerContext;
