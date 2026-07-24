using System.Text.Json.Serialization;

namespace Parrot.Auth;

// Source generation only: MIGRATION.md section 2 forbids reflection-based
// serialization, and the AOT analyzer enforces it.
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class CredentialJsonContext : JsonSerializerContext;
