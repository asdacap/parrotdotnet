using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(RequestWritePermissionToolInput))]
internal sealed partial class RequestWritePermissionJsonContext : JsonSerializerContext;
