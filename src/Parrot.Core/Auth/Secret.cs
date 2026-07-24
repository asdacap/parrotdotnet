using System.Text.Json.Serialization;

namespace Parrot.Auth;

// A string that redacts itself in all formatting. Value is used only where a
// credential is persisted or sent to its issuer.
[JsonConverter(typeof(SecretJsonConverter))]
internal readonly record struct Secret(string Value)
{
    public override string ToString() => "[REDACTED]";
}
