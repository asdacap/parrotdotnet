using System.Text.Json.Serialization;

namespace Parrot.Tools;

internal sealed class QuestionWireOption
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }
}
