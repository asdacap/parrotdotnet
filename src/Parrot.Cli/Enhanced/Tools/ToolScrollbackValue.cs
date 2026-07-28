namespace Parrot.Cli.Enhanced.Tools;

internal sealed class ToolScrollbackValue(
    string label,
    IEnumerable<string> details,
    ToolTerminalStatus status) : IScrollbackItem
{
    private readonly IReadOnlyList<string> _details = ToolDisplayText.Details(details);
    private readonly string _label = ToolDisplayText.Label(label);

    public bool IsCompleted => true;

    public bool Continues(IScrollbackItem previous) => false;

    public IReadOnlyList<string> Render(ScrollbackRenderContext context)
    {
        var (marker, style) = status switch
        {
            ToolTerminalStatus.Succeeded => ("+", context.Palette.Success),
            ToolTerminalStatus.Cancelled => ("-", context.Palette.Muted),
            ToolTerminalStatus.Errored or ToolTerminalStatus.ReportedFailure => ("!", context.Palette.Failure),
            _ => ("!", context.Palette.Failure),
        };
        var lines = new List<string> { style.Apply($"{marker} {_label}") };
        lines.AddRange(_details.SelectMany(detail => TerminalText.Layout($"  {detail}", context.Columns))
            .Select(style.Apply));
        return lines;
    }
}
