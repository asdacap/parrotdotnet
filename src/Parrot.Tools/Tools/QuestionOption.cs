using System.Text.Json.Serialization;

namespace Parrot.Tools;

[JsonConverter(typeof(WireQuestionOptionConverter))]
internal sealed class QuestionOption
{
    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;
}
