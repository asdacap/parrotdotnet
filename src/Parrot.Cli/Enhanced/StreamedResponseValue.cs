namespace Parrot.Cli.Enhanced;

internal readonly record struct StreamedResponseValue(string Marker, string Text) : ILiveBufferItem
{
    public MultiLine Render(LiveBufferRenderContext context)
    {
        var viewport = Viewport(SingleLine(Text), context.Decoration.ContentColumns(context.Columns));
        return new MultiLine(
            [.. context.Decoration.Apply(Marker, [viewport])
                .Select(line => new TerminalLine(line, context.Palette.LiveSurface))],
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
