using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Parrot.Cli.Enhanced;

internal sealed class DiffScrollbackValue(string status, string diff) : IScrollbackItem
{
    internal const int MaximumRows = 100;

    private const int MaximumSourceCharacters = 512 * 1024;
    private const int MinimumSideBySideColumns = 40;
    private static readonly Regex HunkHeader = new(
        "^@@ -(?<oldStart>\\d+)(?:,(?<oldCount>\\d+))? \\+(?<newStart>\\d+)(?:,(?<newCount>\\d+))? @@",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex ChunkReport = new(
        "^Chunk \\d+ has \\d+ match(?:es)?\\.$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public bool IsCompleted => true;

    public ScrollbackLayout Layout => ScrollbackLayout.Block;

    public static IScrollbackItem Create(string status, string diff) =>
        new DiffScrollbackValue(status, diff);

    public bool Continues(IScrollbackItem previous) => false;

    public IReadOnlyList<string> Render(ScrollbackRenderContext context)
    {
        var output = new List<string>();
        var cleanStatus = TerminalText.Sanitize(status).TrimEnd('\r', '\n');
        if (cleanStatus.Length > 0)
        {
            output.AddRange(TerminalText.LayoutWords(cleanStatus, context.Columns).Select(context.Palette.Muted.Apply));
        }

        var (source, sourceOmitted) = BoundSource(diff);
        var clean = TerminalText.Sanitize(source).TrimEnd('\r', '\n');
        if (clean.Trim().Length == 0)
        {
            return output;
        }

        var lines = RenderDiff(clean, context.Columns, context.InlineDiff);
        var limit = Math.Min(lines.Count, MaximumRows);
        for (var index = 0; index < limit; index++)
        {
            var line = lines[index];
            var style = line.Muted ? context.Palette.Muted : Style(line.Text, context.Palette);
            output.Add(style.Apply(TerminalText.Clip(line.Text, context.Columns)));
        }

        if (lines.Count > limit)
        {
            var omitted = (lines.Count - limit).ToString(CultureInfo.InvariantCulture);
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

    private static List<RenderedRow> RenderDiff(string raw, int columns, bool inlineDiff)
    {
        var source = raw.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var reportCount = CountChunkReports(source);
        var diffSource = reportCount > 0 ? source[(reportCount + 1)..] : source;
        var rendered = RenderStructuredDiff(diffSource, columns, inlineDiff);
        if (rendered is null)
        {
            return [.. RawRows(source).Select(line => new RenderedRow(line, false))];
        }

        var output = new List<RenderedRow>(reportCount + rendered.Count);
        output.AddRange(source.Take(reportCount).Select(line => new RenderedRow(line, true)));
        output.AddRange(rendered.Select(line => new RenderedRow(line, false)));
        return output;
    }

    private static int CountChunkReports(string[] source)
    {
        var count = 0;
        while (count < source.Length && ChunkReport.IsMatch(source[count]))
        {
            count++;
        }

        return count > 0 && count < source.Length && source[count].Length == 0 ? count : 0;
    }

    private static List<string>? RenderStructuredDiff(string[] source, int columns, bool inlineDiff)
    {
        var files = new List<DiffFile>();

        for (var index = 0; index < source.Length;)
        {
            if (!source[index].StartsWith("--- ", StringComparison.Ordinal)
                || index + 1 >= source.Length
                || !source[index + 1].StartsWith("+++ ", StringComparison.Ordinal))
            {
                return null;
            }

            var oldPath = source[index][4..];
            var newPath = source[index + 1][4..];
            index += 2;
            var hunks = new List<DiffHunk>();

            if (index < source.Length && string.Equals(source[index], "Binary files differ", StringComparison.Ordinal))
            {
                return null;
            }

            while (index < source.Length && !source[index].StartsWith("--- ", StringComparison.Ordinal))
            {
                var match = HunkHeader.Match(source[index]);
                if (!match.Success)
                {
                    return null;
                }

                var oldStart = int.Parse(match.Groups["oldStart"].Value, CultureInfo.InvariantCulture);
                var oldCount = Count(match.Groups["oldCount"]);
                var newStart = int.Parse(match.Groups["newStart"].Value, CultureInfo.InvariantCulture);
                var newCount = Count(match.Groups["newCount"]);
                index++;
                var rows = new List<DiffRow>();

                while (index < source.Length && !source[index].StartsWith("@@ ", StringComparison.Ordinal)
                    && !source[index].StartsWith("--- ", StringComparison.Ordinal))
                {
                    if (source[index].Length == 0 || source[index][0] is not (' ' or '-' or '+'))
                    {
                        return null;
                    }

                    rows.Add(new DiffRow(source[index][0], source[index][1..]));
                    index++;
                }

                hunks.Add(new DiffHunk(oldStart, oldCount, newStart, newCount, rows));
            }

            files.Add(new DiffFile(oldPath, newPath, hunks));
        }

        if (files.Count == 0)
        {
            return null;
        }

        return inlineDiff || columns < MinimumSideBySideColumns
            ? RenderInline(files)
            : RenderSideBySide(files, columns);
    }

    private static List<string> RenderInline(IReadOnlyList<DiffFile> files)
    {
        var output = new List<string>();
        foreach (var file in files)
        {
            output.Add(FileHeading(file));
            foreach (var hunk in file.Hunks)
            {
                output.Add(HunkHeading(hunk));
                var oldLine = hunk.OldStart;
                var newLine = hunk.NewStart;
                foreach (var row in hunk.Rows)
                {
                    var line = row.Kind switch
                    {
                        ' ' => $"{newLine}  {ExpandTabs(row.Text)}",
                        '-' => $"{oldLine} -{ExpandTabs(row.Text)}",
                        '+' => $"{newLine} +{ExpandTabs(row.Text)}",
                        _ => throw new InvalidOperationException("Unknown diff row."),
                    };
                    output.Add(line);
                    if (row.Kind != '+')
                    {
                        oldLine++;
                    }

                    if (row.Kind != '-')
                    {
                        newLine++;
                    }
                }
            }
        }

        return output;
    }

    private static List<string> RenderSideBySide(IReadOnlyList<DiffFile> files, int columns)
    {
        var output = new List<string>();
        var maximumLine = files.SelectMany(file => file.Hunks)
            .SelectMany(hunk => new[] { hunk.OldStart + hunk.Rows.Count, hunk.NewStart + hunk.Rows.Count })
            .DefaultIfEmpty(1)
            .Max();
        var digits = maximumLine.ToString(CultureInfo.InvariantCulture).Length;
        var cellWidth = (columns - TerminalText.Width(" │ ")) / 2;
        var contentWidth = cellWidth - digits - 2;
        if (contentWidth < 4)
        {
            return RenderInline(files);
        }

        foreach (var file in files)
        {
            output.Add(FileHeading(file));
            foreach (var hunk in file.Hunks)
            {
                output.Add(HunkHeading(hunk));
                var oldLine = hunk.OldStart;
                var newLine = hunk.NewStart;
                for (var index = 0; index < hunk.Rows.Count;)
                {
                    var row = hunk.Rows[index];
                    if (row.Kind == ' ')
                    {
                        output.Add(Cell(oldLine++, ' ', row.Text, digits, contentWidth)
                            + " │ "
                            + Cell(newLine++, ' ', row.Text, digits, contentWidth));
                        index++;
                        continue;
                    }

                    var removed = new List<(int Line, string Text)>();
                    var added = new List<(int Line, string Text)>();
                    while (index < hunk.Rows.Count && hunk.Rows[index].Kind != ' ')
                    {
                        var changed = hunk.Rows[index++];
                        if (changed.Kind == '-')
                        {
                            removed.Add((oldLine++, changed.Text));
                        }
                        else
                        {
                            added.Add((newLine++, changed.Text));
                        }
                    }

                    for (var pair = 0; pair < Math.Max(removed.Count, added.Count); pair++)
                    {
                        var left = pair < removed.Count
                            ? Cell(removed[pair].Line, '-', removed[pair].Text, digits, contentWidth)
                            : string.Empty.PadRight(cellWidth);
                        var right = pair < added.Count
                            ? Cell(added[pair].Line, '+', added[pair].Text, digits, contentWidth)
                            : string.Empty.PadRight(cellWidth);
                        output.Add(left + " │ " + right);
                    }
                }
            }
        }

        return output;
    }

    private static string FileHeading(DiffFile file)
    {
        var oldPath = TrimPath(file.OldPath);
        var newPath = TrimPath(file.NewPath);
        return string.Equals(oldPath, newPath, StringComparison.Ordinal) ? newPath : $"{oldPath} → {newPath}";
    }

    private static string TrimPath(string path) => path.StartsWith("a/", StringComparison.Ordinal)
        || path.StartsWith("b/", StringComparison.Ordinal)
            ? path[2..]
            : path;

    private static string HunkHeading(DiffHunk hunk) =>
        $"@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@";

    private static int Count(Group group) => group.Success
        ? int.Parse(group.Value, CultureInfo.InvariantCulture)
        : 1;

    private static string Cell(int line, char marker, string text, int digits, int contentWidth)
    {
        var prefix = $"{line.ToString(CultureInfo.InvariantCulture).PadLeft(digits)} {marker}";
        var content = TerminalText.Clip(ExpandTabs(text), contentWidth);
        return prefix + content.PadRight(contentWidth);
    }

    private static TerminalStyle Style(string line, TerminalPalette palette)
    {
        if (IsChange(line, '+') || (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal)))
        {
            return palette.DiffAdded;
        }

        if (IsChange(line, '-') || (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal)))
        {
            return palette.DiffRemoved;
        }

        return line.StartsWith("@@", StringComparison.Ordinal)
            || line.StartsWith("---", StringComparison.Ordinal)
            || line.StartsWith("+++", StringComparison.Ordinal)
                ? palette.Muted
                : default;
    }

    private static bool IsChange(string line, char marker)
    {
        var index = 0;
        while (index < line.Length && char.IsDigit(line[index]))
        {
            index++;
        }

        return index > 0 && index + 1 < line.Length && line[index] == ' ' && line[index + 1] == marker;
    }

    private static List<string> RawRows(string[] source) => [.. source.Select(ExpandTabs)];

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

    private readonly record struct DiffFile(string OldPath, string NewPath, IReadOnlyList<DiffHunk> Hunks);

    private readonly record struct DiffHunk(
        int OldStart,
        int OldCount,
        int NewStart,
        int NewCount,
        IReadOnlyList<DiffRow> Rows);

    private readonly record struct DiffRow(char Kind, string Text);

    private readonly record struct RenderedRow(string Text, bool Muted);
}
