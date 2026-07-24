using System.Text.Json.Serialization;

namespace Parrot.Auth;

internal sealed record ApiKeyCredential
{
    [JsonPropertyName("key")]
    public Secret Key { get; init; }
}
