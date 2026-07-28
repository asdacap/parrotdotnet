namespace Parrot.Cli.Enhanced;

internal readonly record struct SpinnerValue(string Activity, int Frame) : ILiveBufferItem
{
    private const string Frames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";

    public string Render() => $"{Frames[Frame % Frames.Length]} {TerminalText.Sanitize(Activity)}";

    public MultiLine Render(LiveBufferRenderContext context) => new(
        [new TerminalLine(Render(), context.Palette.Marker)],
        null,
        LiveBufferRetention.Tail);
}
