using System.Text.Json.Serialization;

namespace Parrot.Tools;

internal sealed class RequestWritePermissionToolInput
{
    [JsonPropertyName("paths")]
    public string[]? Paths { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
