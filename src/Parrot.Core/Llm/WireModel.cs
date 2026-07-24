using System.Text.Json.Serialization;

namespace Parrot.Llm;

internal sealed class WireModel
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}
