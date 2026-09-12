namespace Parrot.Tools;

internal static class ToolInputConversion
{
    public static void RequireObject(string json, string requiredProperty)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            throw new FormatException($"Tool arguments require a string '{requiredProperty}'.");
        }
    }

    public static TimeSpan? ConvertDelay(long? milliseconds, string name)
    {
        const long maxDelayMilliseconds = uint.MaxValue - 1L;
        if (milliseconds is null)
        {
            return null;
        }

        if (milliseconds is < 0 or > maxDelayMilliseconds)
        {
            throw new FormatException(
                $"Tool argument '{name}' must be a non-negative integer no greater than {maxDelayMilliseconds}.");
        }

        return TimeSpan.FromMilliseconds(milliseconds.Value);
    }
}
