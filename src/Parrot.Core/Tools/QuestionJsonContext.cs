using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(QuestionToolInput))]
[JsonSerializable(typeof(QuestionToolResult))]
internal sealed partial class QuestionJsonContext : JsonSerializerContext;
