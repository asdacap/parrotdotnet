using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ReadToolInput))]
[JsonSerializable(typeof(GlobToolInput))]
[JsonSerializable(typeof(GrepToolInput))]
[JsonSerializable(typeof(WebFetchToolInput))]
internal sealed partial class FileToolJsonContext : JsonSerializerContext;
