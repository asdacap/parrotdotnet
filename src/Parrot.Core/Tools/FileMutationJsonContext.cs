using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(WriteToolInput))]
[JsonSerializable(typeof(EditToolInput))]
internal sealed partial class FileMutationJsonContext : JsonSerializerContext;
