namespace Parrot.Cli.Enhanced;

internal readonly record struct PromptValue(PromptState State) : ILiveBufferItem
{
    public PromptValue(string prefix, string text, int cursor)
        : this(new PromptState(prefix, text, cursor))
    {
    }

    public MultiLine Render(LiveBufferRenderContext context)
    {
        var clean = State.Sanitize();
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
