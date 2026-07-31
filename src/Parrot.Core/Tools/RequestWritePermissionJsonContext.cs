using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(RequestWritePermissionTool.Input), TypeInfoPropertyName = "RequestWritePermissionToolInput")]
internal sealed partial class RequestWritePermissionJsonContext : JsonSerializerContext;
