using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ReadTool.Input), TypeInfoPropertyName = "ReadToolInput")]
[JsonSerializable(typeof(ReadImageTool.Input), TypeInfoPropertyName = "ReadImageToolInput")]
[JsonSerializable(typeof(ImageGenerationTool.Input), TypeInfoPropertyName = "ImageGenerationToolInput")]
[JsonSerializable(typeof(GlobTool.Input), TypeInfoPropertyName = "GlobToolInput")]
[JsonSerializable(typeof(WebFetchTool.Input), TypeInfoPropertyName = "WebFetchToolInput")]
internal sealed partial class FileToolJsonContext : JsonSerializerContext;
