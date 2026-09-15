using System.Globalization;
using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class WriteStdinToolPresenter : IToolPresenter
{
    public string ToolName => "write_stdin";

    public ToolPresentationMetadata Metadata { get; } = ToolPresentationMetadata.Default with
    {
        RedactedInputFields = ["input"],
        SuppressTerminalDetails = true,
    };

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
        new ToolLiveValue(Describe(call), [], Metadata, frame);

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal) =>
        new ToolScrollbackValue(Describe(call), ToolBlock.Empty, terminal.ResolveStatus(), Metadata);

    private static string Describe(ToolCallPresentation call)
    {
        var (name, count) = Read(call.ArgumentsJson);
        var process = name.Length == 0 ? "process" : name;
        var characters = count < 0
            ? "input"
            : $"{count.ToString(CultureInfo.InvariantCulture)} chars";
        return $"write {characters} to {process}";
    }

    private static (string Name, int Count) Read(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (string.Empty, -1);
            }

            var name = root.TryGetProperty("name", out var nameValue)
                && nameValue.ValueKind == JsonValueKind.String
                    ? nameValue.GetString() ?? string.Empty
                    : string.Empty;
            var count = root.TryGetProperty("input", out var inputValue)
                && inputValue.ValueKind == JsonValueKind.String
                    ? Count(inputValue.GetString() ?? string.Empty)
                    : -1;
            return (name, count);
        }
        catch (JsonException)
        {
            return (string.Empty, -1);
        }
    }

    private static int Count(string input)
    {
        const string prefix = "<redacted: ";
        const string suffix = " chars>";
        if (input.StartsWith(prefix, StringComparison.Ordinal)
            && input.EndsWith(suffix, StringComparison.Ordinal)
            && int.TryParse(
                input.AsSpan(prefix.Length, input.Length - prefix.Length - suffix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var redactedCount))
        {
            return redactedCount;
        }

        return input.EnumerateRunes().Count();
    }
}
