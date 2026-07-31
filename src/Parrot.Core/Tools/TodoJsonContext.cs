using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(TodoWriteTool.Input), TypeInfoPropertyName = "TodoWriteInput")]
[JsonSerializable(typeof(TodoReadTool.Input), TypeInfoPropertyName = "TodoReadInput")]
[JsonSerializable(typeof(TodoWireItem[]))]
internal sealed partial class TodoJsonContext : JsonSerializerContext;
