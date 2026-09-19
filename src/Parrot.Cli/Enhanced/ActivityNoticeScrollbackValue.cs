namespace Parrot.Cli.Enhanced;

/// <summary>A one-line muted notice about an agent's activity, led by its outcome marker.</summary>
internal sealed class ActivityNoticeScrollbackValue(string marker, string text) : IScrollbackItem
{
    public bool IsCompleted => true;

    public bool Continues(IScrollbackItem previous) => false;

    public IReadOnlyList<string> Render(ScrollbackRenderContext context) =>
        [.. context.Decoration.Apply(
                marker,
                TerminalText.LayoutWords(TerminalText.Sanitize(text), context.Decoration.ContentColumns(context.Columns)))
            .Select(context.Palette.Muted.Apply)];
}
