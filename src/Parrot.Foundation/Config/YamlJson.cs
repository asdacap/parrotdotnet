using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace Parrot.Config;

// Serializes a YAML node to a JSON string, used for the opaque
// provider_preferences blob a user may write as a nested YAML object. Scalars
// are inferred as bool, number, or string. Reflection-free, so AOT-safe.
internal static class YamlJson
{
    public static string Serialize(YamlNode node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, node);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Write(Utf8JsonWriter writer, YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                writer.WriteStartObject();

                foreach (var entry in mapping.Children)
                {
                    if (entry.Key is YamlScalarNode { Value: { } key })
                    {
                        writer.WritePropertyName(key);
                        Write(writer, entry.Value);
                    }
                }

                writer.WriteEndObject();
                break;

            case YamlSequenceNode sequence:
                writer.WriteStartArray();

                foreach (var item in sequence.Children)
                {
                    Write(writer, item);
                }

                writer.WriteEndArray();
                break;

            case YamlScalarNode { Value: { } scalar }:
                WriteScalar(writer, scalar);
                break;

            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static void WriteScalar(Utf8JsonWriter writer, string scalar)
    {
        if (scalar is "true" or "false")
        {
            writer.WriteBooleanValue(scalar == "true");
        }
        else if (long.TryParse(scalar, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            writer.WriteNumberValue(integer);
        }
        else if (double.TryParse(scalar, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            writer.WriteNumberValue(number);
        }
        else
        {
            writer.WriteStringValue(scalar);
        }
    }
}
