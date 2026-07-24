using System.Globalization;
using System.Text.Json;

namespace Parrot.Llm;

// Small tolerant readers over JsonElement, shared by the model-list decoders and
// the usage parsers. Reflection-free, so AOT-safe.
internal static class JsonRead
{
    public static string String(JsonElement scope, string name) =>
        scope.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    public static int Int(JsonElement scope, string name) =>
        scope.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : 0;

    public static long Long(JsonElement scope, string name) =>
        scope.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : 0;

    public static bool Bool(JsonElement scope, string name) =>
        scope.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public static double Number(JsonElement scope, string name)
    {
        if (!scope.TryGetProperty(name, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => double.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
            _ => 0,
        };
    }

    public static IReadOnlyList<string> StringArray(JsonElement scope, string name)
    {
        if (!scope.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return
        [
            .. value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty),
        ];
    }
}
