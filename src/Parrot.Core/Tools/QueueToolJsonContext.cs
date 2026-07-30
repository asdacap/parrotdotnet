using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(QueueCreateToolInput))]
[JsonSerializable(typeof(QueueInfoToolInput))]
[JsonSerializable(typeof(QueueListenToolInput))]
[JsonSerializable(typeof(QueuePushToolInput))]
[JsonSerializable(typeof(QueueTakeToolInput))]
[JsonSerializable(typeof(QueueToolInfo))]
[JsonSerializable(typeof(QueueTakeToolResult))]
internal sealed partial class QueueToolJsonContext : JsonSerializerContext;
