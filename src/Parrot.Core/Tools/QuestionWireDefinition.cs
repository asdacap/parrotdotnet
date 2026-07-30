using System.Text.Json.Serialization;

namespace Parrot.Tools;

internal sealed class QuestionWireDefinition
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("header")]
    public string? Header { get; init; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; init; }

    [JsonPropertyName("options")]
    public QuestionWireOption[]? Options { get; init; }

    [JsonPropertyName("multiple")]
    public bool Multiple { get; init; }

    [JsonPropertyName("custom")]
    public bool Custom { get; init; }
}
