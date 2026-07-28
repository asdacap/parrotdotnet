using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class ApplyPatchToolPresenter : IToolPresenter
{
    public string ToolName => "apply_patch";

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var patch = Patch(call.ArgumentsJson);
        return new ToolLiveValue($"{call.Owner}: apply patch", [patch], frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var patch = Patch(call.ArgumentsJson);
        return new ToolScrollbackValue(
            $"{call.Owner}: apply patch",
            Details(patch, terminal),
            Status(terminal));
    }

    private static string Patch(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("patchText", out var patch)
            && patch.ValueKind == JsonValueKind.String
            ? patch.GetString() ?? string.Empty
            : throw new FormatException("apply_patch requires a string patchText.");
    }

    private static IEnumerable<string> Details(string patch, ToolTerminalPresentation terminal)
    {
        yield return patch;
        if (terminal.ResultPresent)
        {
            yield return terminal.Result;
        }

        if (terminal.Error.Length > 0)
        {
            yield return terminal.Error;
        }
    }

    private static ToolTerminalStatus Status(ToolTerminalPresentation terminal) =>
        terminal.ResultPresent && terminal.Result.StartsWith("error: ", StringComparison.Ordinal)
            ? ToolTerminalStatus.ReportedFailure
            : terminal.Status;
}
