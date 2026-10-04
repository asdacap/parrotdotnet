namespace Parrot.Cli.Enhanced.Tools;

internal sealed class DiffToolScrollbackValue(string toolName, string path, string diff) : IScrollbackItem
{
    public bool IsCompleted => true;

    public ScrollbackLayout Layout => ScrollbackLayout.Block;

    public bool EndsLayout => false;

    public object PackingIdentity => new DiffPacking(toolName, path);

    public bool Continues(IScrollbackItem previous) => false;

    public bool Packs(object? previousPacking) =>
        previousPacking is DiffPacking packing && string.Equals(packing.ToolName, toolName, StringComparison.Ordinal);

    public IReadOnlyList<string> Render(ScrollbackRenderContext context)
    {
        if (!Packs(context.PreviousPacking))
        {
            return new ToolScrollbackValue(
                $"{toolName} {path}",
                ToolBlock.FromDiff(diff),
                ToolTerminalStatus.Succeeded,
                ToolPresentationMetadata.Default).Render(context);
        }

        var content = context with
        {
            Columns = context.Decoration.ContentColumns(context.Columns),
            Decoration = ActivityDecoration.None,
        };
        var heading = Equals(context.PreviousPacking, PackingIdentity) ? string.Empty : path;
        return context.Decoration.Apply(string.Empty, DiffScrollbackValue.Create(heading, diff).Render(content));
    }

    private sealed record DiffPacking(string ToolName, string Path);
}
