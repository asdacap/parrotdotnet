namespace Parrot.Cli.Enhanced;

internal sealed class ProcessCompletionScrollbackValue(string command) : IScrollbackItem
{
    public bool IsCompleted => true;

    public ScrollbackLayout Layout => ScrollbackLayout.Compact;

    public bool Continues(IScrollbackItem previous) => false;

    public IReadOnlyList<string> Render(ScrollbackRenderContext context) =>
        [.. TerminalText.Layout(TerminalText.Sanitize(command), context.Columns)
            .Select(context.Palette.Muted.Apply)];
}
