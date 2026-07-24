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

        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    public int? OptionalInt(string name)
    {
        var root = _document.RootElement;

        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var result)
            ? result
            : null;
    }

    public void Dispose() => _document.Dispose();
}
