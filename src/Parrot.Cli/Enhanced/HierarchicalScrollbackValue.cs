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
        var decoration = HierarchicalActivityValue.Describe(context.Columns, depth, label, string.Empty);
        return [.. value.Render(context with
            {
                Columns = Math.Max(1, context.Columns - decoration.Width + 2),
                ActivityOwner = owner,
            })
            .Select((line, index) => HierarchicalActivityValue.Decorate(
                line,
                decoration,
                successfulIcon,
                index == 0,
                context.Columns).Text)];
    }
}
