using System.Text.Json.Serialization;

namespace Parrot.Auth;

// Source generation only (MIGRATION.md section 2). Unknown fields are rejected
// so a malformed or tampered store fails loudly rather than silently dropping
// data.
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CredentialStoreFile))]
internal sealed partial class CredentialJsonContext : JsonSerializerContext;
