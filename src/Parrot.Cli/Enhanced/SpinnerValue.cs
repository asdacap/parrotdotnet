namespace Parrot.Cli.Enhanced;

internal readonly record struct SpinnerValue(string Activity, int Frame) : ILiveBufferItem
{
    public string Render() =>
        $"{TerminalIcons.SpinnerFrames[Frame % TerminalIcons.SpinnerFrames.Length]} {TerminalText.Sanitize(Activity)}";

    public MultiLine Render(LiveBufferRenderContext context) => new(
        [.. context.Decoration.Apply(
                TerminalIcons.SpinnerFrames[Frame % TerminalIcons.SpinnerFrames.Length].ToString(),
                [TerminalText.Clip(TerminalText.Sanitize(Activity), context.Decoration.ContentColumns(context.Columns))])
            .Select(line => new TerminalLine(line, context.Palette.Marker))],
        null,
        LiveBufferRetention.Tail);
}
