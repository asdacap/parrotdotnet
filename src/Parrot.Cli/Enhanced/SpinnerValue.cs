namespace Parrot.Cli.Enhanced;

internal readonly record struct SpinnerValue(string Activity, int Frame) : ILiveBufferItem
{
    public string Render() =>
        $"{TerminalIcons.SpinnerFrames[Frame % TerminalIcons.SpinnerFrames.Length]} {TerminalText.Sanitize(Activity)}";

    public MultiLine Render(LiveBufferRenderContext context) => new(
        [new TerminalLine(TerminalText.Clip(Render(), context.Columns), context.Palette.Marker)],
        null,
        LiveBufferRetention.Tail);
}
