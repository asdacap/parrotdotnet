namespace Parrot.Cli.Enhanced;

internal sealed class HierarchicalLiveValue(
    ILiveBufferItem value,
    int depth,
    string? label,
    string owner,
    string? successfulIcon,
    LiveModelAliasIcon? modelAliasIcon) : ILiveBufferItem
{
    public MultiLine Render(LiveBufferRenderContext context)
    {
        var cleanLabel = label is null ? string.Empty : TerminalText.Sanitize(label).Replace("\n", string.Empty, StringComparison.Ordinal);
        var glyph = modelAliasIcon is null
            ? string.Empty
            : TerminalText.Sanitize(modelAliasIcon.Glyph).Replace("\n", string.Empty, StringComparison.Ordinal);
        var prefixWidth = (depth * 2) + (label is null ? 0 : TerminalText.Width(cleanLabel) + 3) +
            (glyph.Length == 0 ? 0 : TerminalText.Width(glyph) + 1);
        var rendered = value.Render(context with { Columns = Math.Max(1, context.Columns - prefixWidth) });
        var lines = rendered.Lines.Select((line, index) =>
        {
            var text = HierarchicalActivityValue.Format(
                line.Text,
                depth,
                label,
                owner,
                successfulIcon,
                glyph,
                index == 0);
            if (index != 0 || modelAliasIcon is null || glyph.Length == 0)
            {
                return new TerminalLine(text, line.Style, line.StyleSpans);
            }

            var marker = $"{glyph} ";
            var labelMarker = cleanLabel.Length == 0 ? string.Empty : $"[{cleanLabel}] ";
            var labelStart = labelMarker.Length == 0
                ? 0
                : text.IndexOf(labelMarker, StringComparison.Ordinal);
            var glyphSearchStart = labelStart < 0 ? 0 : labelStart + labelMarker.Length;
            var glyphStart = text.IndexOf(marker, glyphSearchStart, StringComparison.Ordinal);
            var spans = glyphStart < 0
                ? line.StyleSpans
                : [.. line.StyleSpans, new TerminalCellStyleSpan(
                    TerminalText.Width(text[..glyphStart]),
                    TerminalText.Width(glyph),
                    context.Palette.GetLiveIconStyle(modelAliasIcon.Color))];
            return new TerminalLine(text, line.Style, spans);
        }).ToList();
        return rendered with { Lines = lines };
    }
}
