using System.Text.Json;

namespace Parrot.Llm.Wire;

// Validates and forwards the opaque provider-preferences blob without coupling
// the wire layer to any one vendor's schema. Port of Go's
// NormalizeProviderPreferences: a non-object value is rejected because the
// "provider" field OpenAI-compatible routers expect is always an object.
internal static class WirePreferences
{
    public static JsonElement? Normalize(string preferences)
    {
        if (string.IsNullOrWhiteSpace(preferences))
        {
            return null;
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(preferences);
        }
        catch (JsonException failure)
        {
            throw new WireProtocolException("provider preferences are not valid JSON", failure);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new WireProtocolException("provider preferences must be a JSON object");
            }

            return document.RootElement.Clone();
        }
    }
}
