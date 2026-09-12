using System.Text.Json.Serialization;

namespace Parrot.Llm;

internal sealed class WireModelList
{
    [JsonPropertyName("data")]
    public IReadOnlyList<WireModel>? Data { get; init; }
}
