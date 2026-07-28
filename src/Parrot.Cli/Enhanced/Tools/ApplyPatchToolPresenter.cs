using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class ApplyPatchToolPresenter : IToolPresenter
{
    public string ToolName => "apply_patch";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default;

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var patch = Patch(call.ArgumentsJson);
        return new ToolLiveValue($"{call.Owner}: apply patch", ToolBlock.FromDiff(patch), Metadata, frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var patch = Patch(call.ArgumentsJson);
        var status = terminal.ResolveStatus();
        var block = status is ToolTerminalStatus.Errored or ToolTerminalStatus.ReportedFailure
            ? terminal.DescribeBlock(ToolBlockKind.None)
            : ToolBlock.FromDiff(patch);
        return new ToolScrollbackValue($"{call.Owner}: apply patch", block, status, Metadata);
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
}
