using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(QuestionTool.Input), TypeInfoPropertyName = "QuestionToolInput")]
[JsonSerializable(typeof(AnswerTool.Input), TypeInfoPropertyName = "AnswerToolInput")]
internal sealed partial class QuestionJsonContext : JsonSerializerContext;
