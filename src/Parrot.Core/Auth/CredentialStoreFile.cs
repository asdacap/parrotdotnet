using System.Text.Json.Serialization;

namespace Parrot.Auth;

// The on-disk shape: a single JSON object mapping a name to a full credential
// union. Divergence from the previous {providerId: "secret"} string map, and
// from the Go binary's state (no compatibility required per MIGRATION.md).
internal sealed class CredentialStoreFile
{
    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("credentials")]
    public Dictionary<string, Credential> Credentials { get; init; } = new(StringComparer.Ordinal);
}
