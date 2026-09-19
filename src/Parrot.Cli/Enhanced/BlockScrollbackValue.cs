namespace Parrot.Cli.Enhanced;

internal sealed class BlockScrollbackValue(string text, bool muted) : IScrollbackItem
{
    public bool IsCompleted => true;

    public ScrollbackLayout Layout => ScrollbackLayout.Block;

    public static IScrollbackItem Text(string text) => new BlockScrollbackValue(text, false);

    public static IScrollbackItem Muted(string text) => new BlockScrollbackValue(text, true);

    public bool Continues(IScrollbackItem previous) => false;

    public IReadOnlyList<string> Render(ScrollbackRenderContext context)
    {
        var clean = TerminalText.Sanitize(text).TrimEnd('\r', '\n');
        var style = muted ? context.Palette.Muted : default;
        return [.. clean.Split('\n').SelectMany(line => TerminalText.LayoutWords(line.TrimEnd('\r'), context.Columns))
            .Select(style.Apply)];
    }
}
