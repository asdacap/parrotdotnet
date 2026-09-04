using System.Globalization;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Parrot.Cli.Enhanced.Tools;

internal static class ToolYamlFormatter
{
    public static string Format(string value) => TryFormat(value, out var formatted) ? formatted : value;

    public static bool TryFormat(string value, out string formatted)
    {
        formatted = value;

        try
        {
            using var document = JsonDocument.Parse(value);
            var stream = new YamlStream(new YamlDocument(ConvertToYaml(document.RootElement)));
            using var buffer = new StringWriter();
            stream.Save(buffer, assignAnchors: false);

            var text = buffer.ToString().TrimEnd();
            if (text.StartsWith("---", StringComparison.Ordinal))
            {
                var lineEnd = text.IndexOf('\n', StringComparison.Ordinal);
                text = lineEnd < 0 ? string.Empty : text[(lineEnd + 1)..];
            }

            if (text.EndsWith("...", StringComparison.Ordinal))
            {
                text = text[..^3].TrimEnd();
            }

            formatted = text;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static YamlNode ConvertToYaml(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var mapping = new YamlMappingNode();
                foreach (var property in element.EnumerateObject())
                {
                    mapping.Add(KeyScalar(property.Name), ConvertToYaml(property.Value));
                }

                return mapping;
            case JsonValueKind.Array:
                var sequence = new YamlSequenceNode();
                foreach (var item in element.EnumerateArray())
                {
                    sequence.Add(ConvertToYaml(item));
                }

                return sequence;
            case JsonValueKind.String:
                return StringScalar(element.GetString() ?? string.Empty);
            case JsonValueKind.Number:
                return new YamlScalarNode(element.GetRawText());
            case JsonValueKind.True:
                return new YamlScalarNode("true");
            case JsonValueKind.False:
                return new YamlScalarNode("false");
            case JsonValueKind.Null:
                return new YamlScalarNode("null");
            default:
                throw new InvalidOperationException($"Unsupported JSON value kind: {element.ValueKind}.");
        }
    }

    private static YamlScalarNode KeyScalar(string value) =>
        RequiresQuotes(value) ? QuotedScalar(value) : new YamlScalarNode(value);

    private static YamlScalarNode StringScalar(string value) => QuotedScalar(value);

    private static YamlScalarNode QuotedScalar(string value) => new(value) { Style = ScalarStyle.DoubleQuoted };

    private static bool RequiresQuotes(string value) =>
        value.Length == 0
        || value.Equals("null", StringComparison.OrdinalIgnoreCase)
        || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
        || value.Equals("no", StringComparison.OrdinalIgnoreCase)
        || value.Equals("on", StringComparison.OrdinalIgnoreCase)
        || value.Equals("off", StringComparison.OrdinalIgnoreCase)
        || value is "~" or "<<"
        || bool.TryParse(value, out _)
        || decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
        || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out _);
}
