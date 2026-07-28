namespace Parrot.Cli.Enhanced;

internal sealed class MarkdownScrollbackValue(string markdown) : IScrollbackItem
{
    public bool IsCompleted => true;

    public ScrollbackLayout Layout => ScrollbackLayout.Assistant;

    public bool Continues(IScrollbackItem previous) => false;

    public IReadOnlyList<string> Render(ScrollbackRenderContext context) =>
        MarkdownRenderer.Render(
            TerminalIcons.AssistantMessage + " ",
            markdown,
            context.Columns,
            context.Palette.ColorEnabled);
}
