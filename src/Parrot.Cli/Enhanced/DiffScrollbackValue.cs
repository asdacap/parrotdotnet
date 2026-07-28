using System.Globalization;
using System.Text;

namespace Parrot.Cli.Enhanced;

internal sealed class DiffScrollbackValue(string status, string diff) : IScrollbackItem
{
    internal const int MaximumRows = 100;

    private const int MaximumSourceCharacters = 512 * 1024;

    public bool IsCompleted => true;

    public ScrollbackLayout Layout => ScrollbackLayout.Block;

    public bool Continues(IScrollbackItem previous) => false;

    public IReadOnlyList<string> Render(ScrollbackRenderContext context)
    {
        var output = new List<string>();
        var cleanStatus = TerminalText.Sanitize(status).TrimEnd('\r', '\n');
        if (cleanStatus.Length > 0)
        {
            output.AddRange(TerminalText.Layout(cleanStatus, context.Columns).Select(context.Palette.Muted.Apply));
        }

        var (source, sourceOmitted) = BoundSource(diff);
        var clean = TerminalText.Sanitize(source).TrimEnd('\r', '\n');
        if (clean.Trim().Length == 0)
        {
            return output;
        }

        var lines = clean.Split('\n');
        var limit = Math.Min(lines.Length, MaximumRows);
        for (var index = 0; index < limit; index++)
        {
            var line = ExpandTabs(lines[index].TrimEnd('\r'));
            var style = Style(line, context.Palette);
            output.Add(style.Apply(TerminalText.Clip(line, context.Columns)));
        }

        if (lines.Length > limit)
        {
            var omitted = (lines.Length - limit).ToString(CultureInfo.InvariantCulture);
            output.Add(context.Palette.Muted.Apply(TerminalText.Clip($"… {omitted} diff rows omitted", context.Columns)));
        }
        else if (sourceOmitted)
        {
            output.Add(context.Palette.Muted.Apply(
                TerminalText.Clip("… additional diff text omitted", context.Columns)));
        }

        return output;
    }

    private static (string Source, bool Omitted) BoundSource(string value)
    {
        var source = new StringBuilder(Math.Min(value.Length, MaximumSourceCharacters));
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > MaximumSourceCharacters)
            {
                return (source.ToString(), true);
            }

            _ = source.Append(rune);
            bytes += rune.Utf8SequenceLength;
        }

        return (source.ToString(), false);
    }

    private static TerminalStyle Style(string line, TerminalPalette palette)
    {
        if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
        {
            return palette.DiffAdded;
        }

        if (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal))
        {
            return palette.DiffRemoved;
        }

        return line.StartsWith("@@", StringComparison.Ordinal)
            || line.StartsWith("---", StringComparison.Ordinal)
            || line.StartsWith("+++", StringComparison.Ordinal)
                ? palette.Muted
                : default;
    }

    private static string ExpandTabs(string value)
    {
        var output = new System.Text.StringBuilder(value.Length);
        var column = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value == '\t')
            {
                var spaces = 4 - (column % 4);
                _ = output.Append(' ', spaces);
                column += spaces;
                continue;
            }

            _ = output.Append(rune);
            column += TerminalText.Width(rune);
        }

        return output.ToString();
    }
}
