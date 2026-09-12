using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parrot.Auth;

// A Secret serializes as its raw string value; redaction is a formatting concern
// only, never a persistence one.
internal sealed class SecretJsonConverter : JsonConverter<Secret>
{
    public override Secret Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? string.Empty);

    public override void Write(Utf8JsonWriter writer, Secret value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
