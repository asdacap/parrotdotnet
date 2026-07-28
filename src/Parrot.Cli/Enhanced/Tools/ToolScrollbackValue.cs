namespace Parrot.Cli.Enhanced.Tools;

internal sealed class ToolScrollbackValue(
    string label,
    ToolBlock block,
    ToolTerminalStatus status,
    ToolPresentationMetadata metadata) : IScrollbackItem, IToolPresentationValue
{
    public ToolScrollbackValue(
        string label,
        IEnumerable<string> details,
        ToolTerminalStatus status)
        : this(label, details, status, ToolPresentationMetadata.Default)
    {
    }

    public ToolScrollbackValue(
        string label,
        IEnumerable<string> details,
        ToolTerminalStatus status,
        ToolPresentationMetadata metadata)
        : this(
            label,
            DescribeBlock(details, status),
            status,
            metadata)
    {
    }

    public ToolReport Report { get; } =
        ToolReport.DescribeTerminal(ToolDisplayText.Label(label), block, status, metadata);

    public bool IsCompleted => true;

    public ScrollbackLayout Layout => Report.Block.Kind == ToolBlockKind.None
        ? ScrollbackLayout.Compact
        : ScrollbackLayout.Block;

    public bool Continues(IScrollbackItem previous) => false;

    public IReadOnlyList<string> Render(ScrollbackRenderContext context)
    {
        var (marker, style) = DescribeStatus(context);
        var header = TerminalText.Layout($"{marker} {Report.Label}", context.Columns).Take(10).ToArray();
        var lines = header.Select(style.Apply).ToList();
        var maximumLines = Report.Block.Kind switch
        {
            ToolBlockKind.Diff => DiffScrollbackValue.MaximumRows + 2,
            ToolBlockKind.Code or ToolBlockKind.Todos or ToolBlockKind.CompletedInput => 100,
            _ => 10,
        };
        lines.AddRange(RenderBlock(context, Math.Max(0, maximumLines - header.Length), style));
        return lines;
    }

    private static ToolBlock DescribeBlock(IEnumerable<string> details, ToolTerminalStatus status)
    {
        var value = ToolBlock.FromDetails(details);
        return status is ToolTerminalStatus.Errored or ToolTerminalStatus.ReportedFailure
            && value.Kind != ToolBlockKind.None
                ? ToolBlock.FromError(value.Text)
                : value;
    }

    private static IReadOnlyList<string> Bound(
        IReadOnlyList<string> lines,
        int maximumLines,
        ScrollbackRenderContext context,
        string truncated)
    {
        if (lines.Count <= maximumLines)
        {
            return lines;
        }

        var bounded = lines.Take(Math.Max(0, maximumLines - 1)).ToList();
        bounded.Add(context.Palette.Muted.Apply(TerminalText.Clip(truncated, context.Columns)));
        return bounded;
    }

    private (string Marker, TerminalStyle Style) DescribeStatus(ScrollbackRenderContext context) =>
        Report.Status switch
        {
            ToolTerminalStatus.Succeeded => (
                Report.Metadata.SuccessIcon.Length == 0 ? TerminalIcons.Success : Report.Metadata.SuccessIcon,
                Report.Metadata.Style == ToolPresentationStyle.Muted
                    ? context.Palette.Muted
                    : context.Palette.Success),
            ToolTerminalStatus.Cancelled => (TerminalIcons.Interrupted, context.Palette.Muted),
            ToolTerminalStatus.Errored or ToolTerminalStatus.ReportedFailure =>
                (TerminalIcons.Failure, context.Palette.Failure),
            _ => (TerminalIcons.Failure, context.Palette.Failure),
        };

    private IEnumerable<string> RenderBlock(
        ScrollbackRenderContext context,
        int maximumLines,
        TerminalStyle statusStyle)
    {
        if (maximumLines == 0 || Report.Block.Kind == ToolBlockKind.None)
        {
            return [];
        }

        return Report.Block.Kind switch
        {
            ToolBlockKind.Diff => Bound(
                new DiffScrollbackValue(string.Empty, Report.Block.Text).Render(context),
                maximumLines,
                context,
                "… diff output truncated"),
            ToolBlockKind.Code => RenderCode(context, maximumLines),
            ToolBlockKind.Todos => RenderTodos(context, maximumLines),
            ToolBlockKind.CompletedInput => RenderCompletedInput(context, maximumLines),
            ToolBlockKind.Error => ToolDisplayText.LayoutDetails(
                [Report.Block.Text], context.Columns, maximumLines).Select(statusStyle.Apply),
            _ => ToolDisplayText.LayoutDetails([Report.Block.Text], context.Columns, maximumLines)
                .Select(statusStyle.Apply),
        };
    }

    private IReadOnlyList<string> RenderCode(ScrollbackRenderContext context, int maximumLines)
    {
        var location = Report.Block.Path.Length == 0
            ? string.Empty
            : Report.Block.Line > 0
                ? $"{Report.Block.Path}:{Report.Block.Line}"
                : Report.Block.Path;
        var source = $"```{Report.Block.Language}\n{Report.Block.Text}\n```";
        var rendered = MarkdownRenderer.Render(string.Empty, source, context.Columns, context.Palette.ColorEnabled)
            .ToList();
        if (location.Length > 0)
        {
            rendered.Insert(0, context.Palette.Muted.Apply(TerminalText.Clip(location, context.Columns)));
        }

        return Bound(rendered, maximumLines, context, "… code output truncated");
    }

    private IEnumerable<string> RenderTodos(ScrollbackRenderContext context, int maximumLines)
    {
        var lines = ToolDisplayText.LayoutDetails([Report.Block.Text], context.Columns, maximumLines);
        return lines.Select(line => line.TrimStart() switch
        {
            ['✓', ..] => context.Palette.Success.Apply(line),
            ['■', ..] => context.Palette.Muted.Apply(line),
            _ => line,
        });
    }

    private IReadOnlyList<string> RenderCompletedInput(ScrollbackRenderContext context, int maximumLines)
    {
        var source = $"```yaml\n{Report.Block.Text}\n```";
        var rendered = MarkdownRenderer.Render(string.Empty, source, context.Columns, context.Palette.ColorEnabled);
        return Bound(rendered, maximumLines, context, "… completed input truncated");
    }
}
