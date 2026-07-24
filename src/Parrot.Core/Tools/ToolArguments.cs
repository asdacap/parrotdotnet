using System.Text.Json;

namespace Parrot.Tools;

internal sealed class ToolArguments(string json) : IDisposable
{
    private readonly JsonDocument _document = JsonDocument.Parse(json);

    public string RequiredString(string name)
    {
        var root = _document.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"Tool arguments require a string '{name}'.");
        }

        return value.GetString() ?? string.Empty;
    }

    public string OptionalString(string name)
    {
        var root = _document.RootElement;

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"Tool argument '{name}' must be a string.");
        }

        return value.GetString() ?? string.Empty;
    }

    public string? OptionalStrictString(string name)
    {
        var root = _document.RootElement;

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"Tool argument '{name}' must be a string.");
        }

        return value.GetString() ?? string.Empty;
    }

    public TimeSpan? OptionalDelay(string name)
    {
        const long maxDelayMilliseconds = uint.MaxValue - 1L;
        var root = _document.RootElement;

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var milliseconds)
            || milliseconds < 0
            || milliseconds > maxDelayMilliseconds)
        {
            throw new FormatException(
                $"Tool argument '{name}' must be a non-negative integer no greater than {maxDelayMilliseconds}.");
        }

        return TimeSpan.FromMilliseconds(milliseconds);
    }

    public int? OptionalInt(string name)
    {
        var root = _document.RootElement;

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw new FormatException($"Tool argument '{name}' must be an integer.");
        }

        return result;
    }

    public void Dispose() => _document.Dispose();
}
