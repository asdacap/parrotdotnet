namespace Parrot.Cli.Enhanced.Tools;

internal sealed class ToolScrollbackValue(
    string label,
    ToolBlock block,
    ToolTerminalStatus status,
    ToolPresentationMetadata metadata) : IScrollbackItem
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

    public bool IsCompleted => true;

    public ScrollbackLayout Layout => Report.Block.Kind == ToolBlockKind.None
        ? ScrollbackLayout.Compact
        : ScrollbackLayout.Block;

    private ToolReport Report { get; } = ToolReport.DescribeTerminal(
        metadata.MultilineLabel ? TerminalText.Sanitize(label) : ToolDisplayText.Label(label),
        block,
        status,
        metadata);

    public bool Continues(IScrollbackItem previous) => false;

    public IReadOnlyList<string> Render(ScrollbackRenderContext context)
    {
        var (marker, style) = DescribeStatus(context);
        var content = context with
        {
            Columns = context.Decoration.ContentColumns(context.Columns),
            Decoration = ActivityDecoration.None,
        };
        var header = LayoutHeader(Report.Label, content.Columns, Report.Metadata.MultilineLabel);
        var decorated = context.Decoration.Apply(marker, [.. header, .. RenderBlock(content, header.Count, style)]);
        return [.. decorated.Take(header.Count).Select(style.Apply), .. decorated.Skip(header.Count)];
    }

    private static List<string> LayoutHeader(string label, int columns, bool multiline)
    {
        var lines = TerminalText.Layout(label, columns);
        if (!multiline || lines.Count <= 5)
        {
            return [.. lines.Take(10)];
        }

        List<string> visible = [.. lines.Take(5)];
        visible.Add($".. {lines.Count - 5} lines truncated.");
        return visible;
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

    private IEnumerable<string> RenderBlock(ScrollbackRenderContext context, int headerLines, TerminalStyle statusStyle)
    {
        if (Report.Block.Kind == ToolBlockKind.Status)
        {
            return TerminalText.Sanitize(Report.Block.Text).Split('\n')
                .SelectMany(line => TerminalText.Layout(line, context.Columns));
        }

        var blockLines = Report.Block.Kind switch
        {
            ToolBlockKind.Diff => DiffScrollbackValue.MaximumRows + 2,
            ToolBlockKind.Code or ToolBlockKind.CompletedInput => 100,
            ToolBlockKind.Queue => 30,
            _ => 10,
        };
        var maximumLines = Math.Max(0, blockLines - headerLines);
        if (maximumLines == 0 || Report.Block.Kind == ToolBlockKind.None)
        {
            return [];
        }

        if (Report.Block.Kind == ToolBlockKind.Diff)
        {
            var diff = DiffScrollbackValue.Create(string.Empty, Report.Block.Text);
            return Bound(diff.Render(context), maximumLines, context, "… diff output truncated");
        }

        return Report.Block.Kind switch
        {
            ToolBlockKind.Code => RenderCode(context, maximumLines),
            ToolBlockKind.Queue => ToolDisplayText.LayoutDetails(
                [Report.Block.Text], context.Columns, maximumLines, maximumLines),
            ToolBlockKind.CompletedInput => RenderCompletedInput(context, maximumLines),
            ToolBlockKind.Error => ToolDisplayText.LayoutDetails(
                [Report.Block.Text], context.Columns, maximumLines).Select(statusStyle.Apply),
            ToolBlockKind.Output => ToolDisplayText.LayoutDetails(
                [Report.Block.Text], context.Columns, maximumLines),
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

    private IReadOnlyList<string> RenderCompletedInput(ScrollbackRenderContext context, int maximumLines)
    {
        var source = $"```yaml\n{Report.Block.Text}\n```";
        var rendered = MarkdownRenderer.Render(string.Empty, source, context.Columns, context.Palette.ColorEnabled);
        return Bound(rendered, maximumLines, context, "… completed input truncated");
    }
}
