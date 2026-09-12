using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(WriteTool.Input), TypeInfoPropertyName = "WriteToolInput")]
[JsonSerializable(typeof(EditTool.Input), TypeInfoPropertyName = "EditToolInput")]
internal sealed partial class FileMutationJsonContext : JsonSerializerContext;
