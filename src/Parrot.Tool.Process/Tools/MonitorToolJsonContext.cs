using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(MonitorTool.Input), TypeInfoPropertyName = "MonitorToolInput")]
internal sealed partial class MonitorToolJsonContext : JsonSerializerContext;
