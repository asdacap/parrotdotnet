using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parrot.Tools;

internal sealed class ProcessEnvironmentJsonConverter : JsonConverter<Dictionary<string, string>>
{
    public override Dictionary<string, string> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new FormatException("Tool argument 'env' must be an object containing string values.");
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString() ?? string.Empty;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
            {
                throw new FormatException("Tool argument 'env' must contain only string values.");
            }

            environment[name] = reader.GetString() ?? string.Empty;
        }

        return environment;
    }

    public override void Write(
        Utf8JsonWriter writer,
        Dictionary<string, string> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var entry in value)
        {
            writer.WriteString(entry.Key, entry.Value);
        }

        writer.WriteEndObject();
    }
}
