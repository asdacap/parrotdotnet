namespace Parrot.Cli.Enhanced;

internal sealed class HierarchicalScrollbackValue(
    IScrollbackItem value,
    int depth,
    string? label,
    string owner,
    string? successfulIcon) : IScrollbackItem
{
    public bool IsCompleted => value.IsCompleted;

    public ScrollbackLayout Layout => value.Layout;

    public bool StartsLayout => value.StartsLayout;

    public bool EndsLayout => value.EndsLayout;

    public bool Continues(IScrollbackItem previous) => false;

    public IReadOnlyList<string> Render(ScrollbackRenderContext context)
    {
        var prefixWidth = (depth * 2) + (label is null ? 0 : label.Length + 3);
        return [.. value.Render(context with { Columns = Math.Max(1, context.Columns - prefixWidth) })
            .Select((line, index) =>
                HierarchicalActivityValue.Format(line, depth, label, owner, successfulIcon, index == 0))];
    }
}
