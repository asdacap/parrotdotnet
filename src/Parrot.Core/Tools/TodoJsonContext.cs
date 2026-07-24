using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(TodoWriteInput))]
[JsonSerializable(typeof(TodoWireItem[]))]
internal sealed partial class TodoJsonContext : JsonSerializerContext;
