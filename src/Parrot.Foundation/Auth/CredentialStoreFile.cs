using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parrot.Auth;

// The on-disk shape: a single JSON object mapping a name to a full credential
// union. Divergence from the previous {providerId: "secret"} string map, and
// from the Go binary's state (no compatibility required per MIGRATION.md).
// Entries stay raw until looked up, so an entry written by a newer build does
// not break the rest and survives a rewrite by an older build.
internal sealed class CredentialStoreFile
{
    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("credentials")]
    public Dictionary<string, JsonElement> Credentials { get; init; } = new(StringComparer.Ordinal);
}
