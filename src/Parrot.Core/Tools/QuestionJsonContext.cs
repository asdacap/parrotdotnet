using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(QuestionTool.Input), TypeInfoPropertyName = "QuestionToolInput")]
internal sealed partial class QuestionJsonContext : JsonSerializerContext;
