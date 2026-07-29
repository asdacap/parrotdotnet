using System.Text.Json.Serialization;

namespace Parrot.Tools;

internal sealed class EditToolInput
{
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("old_string")]
    public string? OldString { get; init; }

    [JsonPropertyName("new_string")]
    public string? NewString { get; init; }

    [JsonPropertyName("replace_all")]
    public bool? ReplaceAll { get; init; }
}
