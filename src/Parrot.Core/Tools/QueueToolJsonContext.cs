using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(QueueToolInput))]
[JsonSerializable(typeof(QueueToolInfo))]
[JsonSerializable(typeof(QueueTakeToolResult))]
internal sealed partial class QueueToolJsonContext : JsonSerializerContext;
