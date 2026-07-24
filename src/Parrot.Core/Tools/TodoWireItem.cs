using System.Text.Json.Serialization;

namespace Parrot.Tools;

internal sealed record TodoWireItem
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("priority")]
    public string? Priority { get; init; }

    [JsonPropertyName("position")]
    public int? Position { get; init; }
}
