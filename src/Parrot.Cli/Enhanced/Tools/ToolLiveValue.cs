namespace Parrot.Cli.Enhanced.Tools;

internal sealed class ToolLiveValue : ILiveBufferItem
{
    private const string Frames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";

    private readonly int _frame;
    private readonly RunningDuration? _runningDuration;

    public ToolLiveValue(string label, IEnumerable<string> details, int frame)
        : this(label, details, ToolPresentationMetadata.Default, frame)
    {
    }

    public ToolLiveValue(
        string label,
        IEnumerable<string> details,
        ToolPresentationMetadata metadata,
        int frame)
        : this(ToolReport.DescribeLive(label, ToolBlock.FromDetails(details), metadata), frame, null)
    {
    }

    public ToolLiveValue(string label, ToolBlock block, ToolPresentationMetadata metadata, int frame)
        : this(ToolReport.DescribeLive(label, block, metadata), frame, null)
    {
    }

    public ToolLiveValue(
        string label,
        IEnumerable<string> details,
        ToolPresentationMetadata metadata,
        int frame,
        RunningDuration runningDuration)
        : this(ToolReport.DescribeLive(label, ToolBlock.FromDetails(details), metadata), frame, runningDuration)
    {
    }

    private ToolLiveValue(ToolReport report, int frame, RunningDuration? runningDuration)
    {
        Report = report with
        {
            Label = report.Metadata.MultilineLabel
                ? TerminalText.Sanitize(report.Label)
                : ToolDisplayText.Label(report.Label),
        };
        _frame = frame;
        _runningDuration = runningDuration;
    }

    private ToolReport Report { get; }

    public ILiveBufferItem Animate(int frame) => new ToolLiveValue(Report, frame, _runningDuration);

    public MultiLine Render(LiveBufferRenderContext context)
    {
        var lines = new List<TerminalLine>();
        var marker = Frames[_frame % Frames.Length];
        var activityLabel = HierarchicalActivityValue.RemoveOwner(Report.Label, context.ActivityOwner);
        var label = _runningDuration is null
            ? activityLabel
            : $"{activityLabel} (running {_runningDuration.Format()})";
        var header = TerminalText.Layout($"{marker} {label}", context.Columns).Take(10).ToArray();
        var headerStyle = Report.Metadata.Style == ToolPresentationStyle.Muted
            ? context.Palette.LiveMuted
            : context.Palette.Marker;
        lines.AddRange(header.Select(value => new TerminalLine(value, headerStyle)));
        var detailLines = ToolDisplayText.LayoutDetails(
            Report.Block.Kind == ToolBlockKind.None ? [] : [Report.Block.Text],
            context.Columns,
            Math.Max(0, 10 - header.Length));
        lines.AddRange(detailLines.Select(value => new TerminalLine(value, context.Palette.LiveSurface)));
        return new MultiLine(lines, null, LiveBufferRetention.Tail);
    }
}
