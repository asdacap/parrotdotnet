using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Parrot.Cli.Enhanced.Tools;

internal static class ToolPresentationRedactor
{
    private const string Redacted = "<redacted>";

    public static ToolCallPresentation Redact(
        ToolCallPresentation call,
        ToolPresentationMetadata metadata)
    {
        if (metadata.RedactedInputFields.Count == 0)
        {
            return call;
        }

        try
        {
            if (JsonNode.Parse(call.ArgumentsJson) is not JsonObject root)
            {
                return call with { ArgumentsJson = Redacted };
            }

            foreach (var field in metadata.RedactedInputFields)
            {
                if (!root.TryGetPropertyValue(field, out var value))
                {
                    continue;
                }

                root[field] = value is JsonValue jsonValue
                    && jsonValue.TryGetValue<string>(out var text)
                        ? $"<redacted: {text.EnumerateRunes().Count()} chars>"
                        : Redacted;
            }

            return call with
            {
                ArgumentsJson = root.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            };
        }
        catch (JsonException)
        {
            return call with { ArgumentsJson = Redacted };
        }
    }

    public static ToolTerminalPresentation Redact(
        ToolTerminalPresentation terminal,
        ToolPresentationMetadata metadata) =>
        metadata.SuppressTerminalDetails
            ? terminal with { ResultPresent = false, Result = string.Empty, Error = string.Empty }
            : terminal;
}
