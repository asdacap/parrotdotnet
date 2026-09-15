namespace Parrot.Cli.Enhanced;

internal sealed class FinalMessageScrollbackValue(string source) : IScrollbackItem
{
    public bool IsCompleted => true;

    public ScrollbackLayout Layout => ScrollbackLayout.Assistant;

    public bool Continues(IScrollbackItem previous) => false;

    public IReadOnlyList<string> Render(ScrollbackRenderContext context) => context.Decoration.Apply(
        TerminalIcons.AssistantMessage,
        EnhancedFinalMessageRenderer.Render(
            source,
            string.Empty,
            context.Decoration.ContentColumns(context.Columns),
            context.Palette.ColorEnabled,
            true));
}
