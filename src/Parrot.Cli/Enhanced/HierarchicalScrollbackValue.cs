namespace Parrot.Cli.Enhanced;

internal sealed class HierarchicalScrollbackValue(IScrollbackItem value, int depth, string? label) : IScrollbackItem
{
    public bool IsCompleted => value.IsCompleted;

    public ScrollbackLayout Layout => value.Layout;

    public bool StartsLayout => value.StartsLayout;

    public bool EndsLayout => value.EndsLayout;

    public bool Continues(IScrollbackItem previous) => false;

    public IReadOnlyList<string> Render(ScrollbackRenderContext context) => value.Render(context with
    {
        Decoration = ActivityDecoration.Describe(context.Columns, depth, label, string.Empty),
    });
}
