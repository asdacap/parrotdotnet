using System.Text.Json.Serialization;

namespace Parrot.Auth;

// Source generation for the small request payloads the OAuth flows serialize.
[JsonSourceGenerationOptions]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class AuthWireContext : JsonSerializerContext;
