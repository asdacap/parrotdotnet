namespace Parrot.Cli.Enhanced.Tools;

internal sealed class ToolLiveValue : ILiveBufferItem
{
    private const string Frames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";

    private readonly IReadOnlyList<string> _details;
    private readonly int _frame;
    private readonly string _label;

    public ToolLiveValue(string label, IEnumerable<string> details, int frame)
        : this(ToolDisplayText.Label(label), ToolDisplayText.Details(details), frame)
    {
    }

    private ToolLiveValue(string label, IReadOnlyList<string> details, int frame)
    {
        _label = label;
        _details = details;
        _frame = frame;
    }

    public ToolLiveValue Animate(int frame) => new(_label, _details, frame);

    public MultiLine Render(LiveBufferRenderContext context)
    {
        var lines = new List<TerminalLine>();
        var marker = Frames[_frame % Frames.Length];
        lines.AddRange(TerminalText.Layout($"{marker} {_label}", context.Columns)
            .Select(value => new TerminalLine(value, context.Palette.Marker)));
        lines.AddRange(_details.SelectMany(detail => TerminalText.Layout($"  {detail}", context.Columns))
            .Select(value => new TerminalLine(value, context.Palette.LiveSurface)));
        return new MultiLine(lines, null, LiveBufferRetention.Tail);
    }
}
