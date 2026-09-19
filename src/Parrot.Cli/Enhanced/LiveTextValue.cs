namespace Parrot.Cli.Enhanced;

internal readonly record struct LiveTextValue(string Text) : ILiveBufferItem
{
    public MultiLine Render(LiveBufferRenderContext context) => new(
        [.. TerminalText.LayoutWords(TerminalText.Sanitize(Text), context.Columns)
            .Select(value => new TerminalLine(value, context.Palette.LiveSurface))],
        null,
        LiveBufferRetention.Tail);
}
