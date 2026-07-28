namespace Parrot.Cli.Enhanced.Tools;

internal sealed class ToolLiveValue : ILiveBufferItem, IToolPresentationValue
{
    private const string Frames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";

    private readonly int _frame;

    public ToolLiveValue(string label, IEnumerable<string> details, int frame)
        : this(label, details, ToolPresentationMetadata.Default, frame)
    {
    }

    public ToolLiveValue(
        string label,
        IEnumerable<string> details,
        ToolPresentationMetadata metadata,
        int frame)
        : this(ToolReport.DescribeLive(label, ToolBlock.FromDetails(details), metadata), frame)
    {
    }

    public ToolLiveValue(string label, ToolBlock block, ToolPresentationMetadata metadata, int frame)
        : this(ToolReport.DescribeLive(label, block, metadata), frame)
    {
    }

    private ToolLiveValue(ToolReport report, int frame)
    {
        Report = report with { Label = ToolDisplayText.Label(report.Label) };
        _frame = frame;
    }

    public ToolReport Report { get; }

    public ToolLiveValue Animate(int frame) => new(Report, frame);

    public MultiLine Render(LiveBufferRenderContext context)
    {
        var lines = new List<TerminalLine>();
        var marker = Frames[_frame % Frames.Length];
        var header = TerminalText.Layout($"{marker} {Report.Label}", context.Columns).Take(10).ToArray();
        lines.AddRange(header.Select(value => new TerminalLine(value, context.Palette.Marker)));
        var detailLines = ToolDisplayText.LayoutDetails(
            Report.Block.Kind == ToolBlockKind.None ? [] : [Report.Block.Text],
            context.Columns,
            Math.Max(0, 10 - header.Length));
        lines.AddRange(detailLines.Select(value => new TerminalLine(value, context.Palette.LiveSurface)));
        return new MultiLine(lines, null, LiveBufferRetention.Tail);
    }
}
