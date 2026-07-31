using System.Text;

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
        if (TerminalText.Width(text) <= width)
        {
            return text;
        }

        var tokenStart = 0;
        var offset = 0;
        var insideToken = false;
        var hasToken = false;
        foreach (var grapheme in TerminalText.EnumerateGraphemes(text))
        {
            if (IsWhitespace(grapheme))
            {
                insideToken = false;
            }
            else if (!insideToken)
            {
                tokenStart = offset;
                insideToken = true;
                hasToken = true;
            }

            offset += grapheme.Length;
        }

        return TerminalText.Clip(hasToken ? text[tokenStart..] : text, width);
    }

    private static bool IsWhitespace(string grapheme) => Rune.IsWhiteSpace(Rune.GetRuneAt(grapheme, 0));
}
