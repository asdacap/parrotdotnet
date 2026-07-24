using System.Text.Json;

namespace Parrot.Tools;

// Reads a file under the working directory. Path traversal outside it is
// refused rather than followed.
internal sealed class ReadFileTool : ITool
{
    public string Name => "read_file";

    public string Description => "Read a UTF-8 text file, relative to the working directory.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"path":{"type":"string","description":"Path relative to the working directory"}},"required":["path"]}
        """;

    public async Task<string> Execute(
        string argumentsJson, IToolContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var relative = ReadString(argumentsJson, "path");
        var root = Path.GetFullPath(context.WorkingDirectory);
        var full = Path.GetFullPath(Path.Combine(root, relative));

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && full != root)
        {
            return "error: path escapes the working directory";
        }

        if (!File.Exists(full))
        {
            return "error: no such file";
        }

        return await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false);
    }

    private static string ReadString(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
