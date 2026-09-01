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
        var glyph = modelAliasIcon?.Glyph ?? string.Empty;
        var decoration = HierarchicalActivityValue.Describe(context.Columns, depth, label, glyph);
        var rendered = value.Render(context with
        {
            Columns = Math.Max(1, context.Columns - decoration.Width + 2),
            ActivityOwner = owner,
        });
        var lines = rendered.Lines.Select((line, index) =>
        {
            var decorated = HierarchicalActivityValue.Decorate(
                line.Text,
                decoration,
                successfulIcon,
                index == 0,
                context.Columns);
            var contentWidth = Math.Max(0, context.Columns - decorated.PrefixWidth);
            var spans = line.StyleSpans
                .Select(span => new TerminalCellStyleSpan(
                    span.StartCell + decorated.PrefixWidth,
                    Math.Min(span.Length, Math.Max(0, contentWidth - span.StartCell)),
                    span.Style))
                .Where(span => span.Length > 0)
                .ToList();
            if (index == 0 && modelAliasIcon is not null && decorated.GlyphWidth > 0)
            {
                spans.Add(new TerminalCellStyleSpan(
                    decorated.GlyphStartCell,
                    decorated.GlyphWidth,
                    context.Palette.GetLiveIconStyle(modelAliasIcon.Color)));
            }

            return new TerminalLine(decorated.Text, line.Style, spans);
        }).ToList();
        return rendered with { Lines = lines };
    }
}
