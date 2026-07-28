namespace Parrot.Cli.Enhanced;

internal readonly record struct DialogMessageValue(string Text, bool Error) : ILiveBufferItem
{
    public MultiLine Render(LiveBufferRenderContext context)
    {
        var lines = TerminalText.Layout(TerminalText.Sanitize(Text), context.Columns);
        var style = Error ? context.Palette.Failure : context.Palette.LiveSurface;
        return new MultiLine(
            [.. lines.Select(value => new TerminalLine(value, style))],
            new LiveBufferCaret(lines.Count - 1, TerminalText.Width(lines[^1])),
            LiveBufferRetention.Caret);
    }
}
