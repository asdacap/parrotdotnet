using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class AgentTaskProgressLiveValue(
    AgentTaskProgressSnapshot snapshot,
    IReadOnlyDictionary<string, TaskAgentLine>? agentLines,
    IReadOnlySet<string>? activeAgentSessionIds) : ILiveBufferItem
{
    public AgentTaskProgressSnapshot Snapshot => snapshot;

    public MultiLine Render(LiveBufferRenderContext context)
    {
        var columns = context.Decoration.ContentColumns(context.Columns);
        var rows = new List<TerminalLine>();
        foreach (var row in AgentTaskProgressFormatter.FormatRows(snapshot, agentLines, activeAgentSessionIds))
        {
            var wrapped = TerminalText.LayoutWordsHanging(row.Text, columns, row.HangingIndent);
            var style = context.Palette.GetTaskStyle(row.Status, true);
            var sourceOffset = 0;
            for (var index = 0; index < wrapped.Count; index++)
            {
                var text = wrapped[index];
                IReadOnlyList<TerminalCellStyleSpan> spans = [];
                if (row.ModelAliasIcon is { } icon)
                {
                    var indentLength = index > 0 && TerminalText.Width(row.HangingIndent) < columns
                        ? row.HangingIndent.Length
                        : 0;
                    var content = text[indentLength..];
                    var sourceStart = row.Text.IndexOf(content, sourceOffset, StringComparison.Ordinal);
                    var glyphStart = Math.Max(row.GlyphStartIndex, sourceStart);
                    var glyphEnd = Math.Min(row.GlyphStartIndex + icon.Glyph.Length, sourceStart + content.Length);
                    if (glyphEnd > glyphStart)
                    {
                        var start = indentLength + glyphStart - sourceStart;
                        spans = [new TerminalCellStyleSpan(
                            context.Decoration.Width + TerminalText.Width(text[..start]),
                            TerminalText.Width(text.Substring(start, glyphEnd - glyphStart)),
                            context.Palette.GetLiveIconStyle(icon.Color))];
                    }

                    sourceOffset = sourceStart + content.Length;
                }

                rows.Add(new TerminalLine(text, style, spans));
            }
        }

        return new MultiLine(
            [.. context.Decoration.Apply(TerminalIcons.Activity, rows.Select(static row => row.Text))
                .Select((text, index) => new TerminalLine(text, rows[index].Style, rows[index].StyleSpans))],
            null,
            LiveBufferRetention.Fixed);
    }
}
