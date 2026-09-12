using System.Globalization;
using System.Text.Json;

namespace Parrot.Llm;

// Small tolerant readers over JsonElement, shared by the model-list decoders and
// the usage parsers. Reflection-free, so AOT-safe.
internal static class JsonRead
{
    public static string String(JsonElement scope, string name) =>
        TryReadString(scope, name, out var value) ? value : string.Empty;

    public static bool TryReadString(JsonElement scope, string name, out string result)
    {
        result = string.Empty;

        if (!scope.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        result = value.GetString() ?? string.Empty;
        return true;
    }

    public static int Int(JsonElement scope, string name) =>
        TryReadInt(scope, name, out var value) ? value : 0;

    public static bool TryReadInt(JsonElement scope, string name, out int result)
    {
        result = 0;
        return scope.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out result);
    }

    public static long Long(JsonElement scope, string name) =>
        scope.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : 0;

    public static bool Bool(JsonElement scope, string name) =>
        TryReadBool(scope, name, out var value) && value;

    public static bool TryReadBool(JsonElement scope, string name, out bool result)
    {
        result = false;

        if (!scope.TryGetProperty(name, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        result = value.GetBoolean();
        return true;
    }

    public static double Number(JsonElement scope, string name) =>
        TryReadNumber(scope, name, out var value) ? value : 0;

    public static bool TryReadNumber(JsonElement scope, string name, out double result)
    {
        result = 0;

        if (!scope.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out result),
            JsonValueKind.String => double.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result),
            _ => false,
        };
    }

    public static bool TryReadNonNegativeNumber(JsonElement scope, string name, out double result)
    {
        if (TryReadNumber(scope, name, out result) && double.IsFinite(result) && result >= 0)
        {
            return true;
        }

        result = 0;
        return false;
    }

    public static IReadOnlyList<string> StringArray(JsonElement scope, string name) =>
        TryReadStringArray(scope, name, out var value) ? value : [];

    public static bool TryReadStringArray(JsonElement scope, string name, out IReadOnlyList<string> result)
    {
        result = [];

        if (!scope.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        result =
        [
            .. value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty),
        ];
        return true;
    }
}
