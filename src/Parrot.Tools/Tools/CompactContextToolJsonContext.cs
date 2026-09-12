using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CompactContextTool.Input), TypeInfoPropertyName = "CompactContextToolInput")]
internal sealed partial class CompactContextToolJsonContext : JsonSerializerContext;
