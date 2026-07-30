using System.Text.Json.Serialization;

namespace Parrot.Tools;

internal sealed class QuestionWireAnswer
{
    [JsonPropertyName("question_id")]
    public required string QuestionId { get; init; }

    [JsonPropertyName("option_ids")]
    public required string[] OptionIds { get; init; }

    [JsonPropertyName("custom")]
    public required string Custom { get; init; }
}
