using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Parrot.Llm.Wire;

internal static class WireEncoding
{
    private static readonly ConditionalWeakTable<string, StrongBox<JsonElement>> Schemas = [];

    public static string ImageReference(LLMContent content) =>
        content.ImageUrl.Length > 0
            ? content.ImageUrl
            : $"data:{content.MediaType};base64,{Convert.ToBase64String(content.ReadImage())}";

    public static JsonElement ParseSchema(string schema) =>
        Schemas.GetValue(schema, static schema => new(JsonElement.Parse(schema.Length > 0 ? schema : "{}"))).Value;
}
