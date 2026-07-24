using System.Text.Json.Serialization;

namespace Parrot.Tools;

internal sealed record TodoWriteInput
{
    [JsonPropertyName("todos")]
    public TodoWireItem[]? Todos { get; init; }
}
