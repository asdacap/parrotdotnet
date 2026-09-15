namespace Parrot.Cli.Enhanced;

internal sealed class HierarchicalLiveValue(
    ILiveBufferItem value,
    int depth,
    string? label,
    LiveModelAliasIcon? modelAliasIcon) : ILiveBufferItem
{
    public MultiLine Render(LiveBufferRenderContext context)
    {
        var decoration = ActivityDecoration.Describe(
            context.Columns,
            depth,
            label,
            modelAliasIcon?.Glyph ?? string.Empty);
        var rendered = value.Render(context with { Decoration = decoration });
        if (modelAliasIcon is null || rendered.Lines.Count == 0 || decoration.GlyphWidth == 0)
        {
            return rendered;
        }

        var lead = rendered.Lines[0];
        var glyph = new TerminalCellStyleSpan(
            decoration.GlyphStartCell,
            decoration.GlyphWidth,
            context.Palette.GetLiveIconStyle(modelAliasIcon.Color));
        return rendered with
        {
            Lines = [new TerminalLine(lead.Text, lead.Style, [.. lead.StyleSpans, glyph]), .. rendered.Lines.Skip(1)],
        };
    }
}
