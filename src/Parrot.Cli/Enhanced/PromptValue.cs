namespace Parrot.Cli.Enhanced;

internal readonly record struct PromptValue(string Prefix, string Text, int Cursor) : ILiveBufferItem
{
    public PromptValue Sanitize()
    {
        var cleanPrefix = TerminalText.Sanitize(Prefix).Replace("\n", string.Empty, StringComparison.Ordinal);
        var cleanText = TerminalText.Sanitize(Text);
        var runeCount = cleanText.EnumerateRunes().Count();
        return new PromptValue(cleanPrefix, cleanText, Math.Clamp(Cursor, 0, runeCount));
    }

    public MultiLine Render(LiveBufferRenderContext context)
    {
        var clean = Sanitize();
        var indent = new string(' ', TerminalText.Width(clean.Prefix));
        var lines = TerminalText.LayoutHanging(clean.Prefix + clean.Text, context.Columns, indent);
        var before = clean.Prefix + string.Concat(clean.Text.EnumerateRunes().Take(clean.Cursor));
        var caretLines = TerminalText.LayoutHanging(before, context.Columns, indent);
        return new MultiLine(
            [.. lines.Select(value => new TerminalLine(value, context.Palette.Prompt))],
            new LiveBufferCaret(caretLines.Count - 1, TerminalText.Width(caretLines[^1])),
            LiveBufferRetention.Caret);
    }
}
