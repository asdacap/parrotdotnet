namespace Parrot.Cli.Enhanced;

internal readonly record struct StreamedResponseValue(string Prefix, string Text) : ILiveBufferItem
{
    public MultiLine Render(LiveBufferRenderContext context)
    {
        var prefix = SingleLine(Prefix);
        var text = SingleLine(Text);
        var prefixWidth = TerminalText.Width(prefix);
        var available = Math.Max(0, context.Columns - prefixWidth);
        var rendered = available == 0
            ? TerminalText.Clip(prefix, context.Columns)
            : prefix + Viewport(text, available);
        return new MultiLine(
            [new TerminalLine(rendered, context.Palette.LiveSurface)],
            null,
            LiveBufferRetention.Tail);
    }

    private static string SingleLine(string value) => TerminalText.Sanitize(value)
        .Replace("\n", " ", StringComparison.Ordinal);

    private static string Viewport(string text, int width)
    {
        var excess = TerminalText.Width(text) - width;
        if (excess <= 0)
        {
            return text;
        }

        var removed = 0;
        var offset = 0;
        foreach (var grapheme in TerminalText.EnumerateGraphemes(text))
        {
            removed += TerminalText.Width(grapheme);
            offset += grapheme.Length;
            if (removed >= excess)
            {
                return text[offset..];
            }
        }

        return string.Empty;
    }
}
