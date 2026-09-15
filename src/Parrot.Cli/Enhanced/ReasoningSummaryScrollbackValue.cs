namespace Parrot.Cli.Enhanced;

internal sealed class ReasoningSummaryScrollbackValue(string markdown) : IScrollbackItem
{
    public bool IsCompleted => true;

    public ScrollbackLayout Layout => ScrollbackLayout.Compact;

    public bool Continues(IScrollbackItem previous) => false;

    public IReadOnlyList<string> Render(ScrollbackRenderContext context) =>
        [.. context.Decoration.Apply(
                TerminalIcons.Reasoning,
                MarkdownRenderer.Render(
                    string.Empty,
                    markdown,
                    context.Decoration.ContentColumns(context.Columns),
                    false))
            .Select(context.Palette.Muted.Apply)];
}
