using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(QueueCreateTool.Input), TypeInfoPropertyName = "QueueCreateToolInput")]
[JsonSerializable(typeof(QueueInfoTool.Input), TypeInfoPropertyName = "QueueInfoToolInput")]
[JsonSerializable(typeof(QueueListenTool.Input), TypeInfoPropertyName = "QueueListenToolInput")]
[JsonSerializable(typeof(QueuePushTool.Input), TypeInfoPropertyName = "QueuePushToolInput")]
[JsonSerializable(typeof(QueueTakeTool.Input), TypeInfoPropertyName = "QueueTakeToolInput")]
[JsonSerializable(typeof(QueueToolInfo))]
[JsonSerializable(typeof(QueueTakeToolResult))]
internal sealed partial class QueueToolJsonContext : JsonSerializerContext;
