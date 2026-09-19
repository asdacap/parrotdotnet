using System.Globalization;
using System.Text;

namespace Parrot.Cli.Enhanced;

internal static class TerminalText
{
    public static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var clean = new StringBuilder(value.Length);

        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value == '\t')
            {
                _ = clean.Append("    ");
            }
            else if (rune.Value == '\n' || !Rune.IsControl(rune))
            {
                _ = clean.Append(rune);
            }
        }

        return clean.ToString();
    }

    public static List<string> LayoutWords(string value, int width) =>
        LayoutWordsHanging(value, width, string.Empty);

    public static List<string> LayoutWordsHanging(string value, int width, string indent)
    {
        ArgumentNullException.ThrowIfNull(value);

        width = Math.Max(1, width);
        indent = Sanitize(indent).Replace("\n", string.Empty, StringComparison.Ordinal);
        if (Width(indent) >= width)
        {
            indent = string.Empty;
        }

        var indentWidth = Width(indent);
        var contentWidth = Math.Max(1, width - indentWidth);
        var rows = new List<string>();
        var row = new StringBuilder();
        var cells = 0;
        var rowStart = 0;

        void FinishRow() => rows.Add(row.ToString());

        void BeginContinuation()
        {
            FinishRow();
            _ = row.Clear().Append(indent);
            cells = indentWidth;
            rowStart = indentWidth;
        }

        void AppendBroken(string text)
        {
            foreach (var grapheme in EnumerateGraphemes(text))
            {
                var graphemeWidth = WidthGrapheme(grapheme);
                if (cells > rowStart && cells + graphemeWidth > width)
                {
                    BeginContinuation();
                }

                _ = row.Append(grapheme);
                cells += graphemeWidth;
            }
        }

        void AppendWord(string word, string spacing)
        {
            var wordWidth = Width(word);
            var gapWidth = Width(spacing);
            if (cells + gapWidth + wordWidth <= width)
            {
                _ = row.Append(spacing).Append(word);
                cells += gapWidth + wordWidth;
                return;
            }

            if (wordWidth > contentWidth)
            {
                if (cells + gapWidth <= width)
                {
                    _ = row.Append(spacing);
                    cells += gapWidth;
                }

                AppendBroken(word);
                return;
            }

            if (cells > rowStart)
            {
                BeginContinuation();
            }

            _ = row.Append(word);
            cells += wordWidth;
        }

        void AppendLine(string line)
        {
            var word = new StringBuilder();
            var spacing = new StringBuilder();
            foreach (var grapheme in EnumerateGraphemes(line))
            {
                if (IsWordBreak(grapheme))
                {
                    if (word.Length > 0)
                    {
                        AppendWord(word.ToString(), spacing.ToString());
                        _ = word.Clear();
                        _ = spacing.Clear();
                    }

                    _ = spacing.Append(grapheme);
                }
                else
                {
                    _ = word.Append(grapheme);
                }
            }

            if (word.Length > 0)
            {
                AppendWord(word.ToString(), spacing.ToString());
                _ = spacing.Clear();
            }

            foreach (var grapheme in EnumerateGraphemes(spacing.ToString()))
            {
                var graphemeWidth = WidthGrapheme(grapheme);
                if (cells + graphemeWidth > width)
                {
                    break;
                }

                _ = row.Append(grapheme);
                cells += graphemeWidth;
            }
        }

        var lines = value.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (index > 0)
            {
                BeginContinuation();
            }

            AppendLine(lines[index]);
        }

        FinishRow();
        return rows;
    }

    public static List<string> Layout(string value, int width) => LayoutHanging(value, width, string.Empty);

    public static List<string> LayoutHanging(string value, int width, string indent)
    {
        width = Math.Max(1, width);
        indent = Sanitize(indent).Replace("\n", string.Empty, StringComparison.Ordinal);
        if (Width(indent) >= width)
        {
            indent = string.Empty;
        }

        var rows = new List<string>();
        var row = new StringBuilder();
        var cells = 0;
        foreach (var grapheme in EnumerateGraphemes(value))
        {
            if (grapheme == "\n")
            {
                rows.Add(row.ToString());
                _ = row.Clear().Append(indent);
                cells = Width(indent);
                continue;
            }

            var graphemeWidth = WidthGrapheme(grapheme);
            if (cells > 0 && cells + graphemeWidth > width)
            {
                rows.Add(row.ToString());
                _ = row.Clear().Append(indent);
                cells = Width(indent);
            }

            _ = row.Append(grapheme);
            cells += graphemeWidth;
        }

        rows.Add(row.ToString());
        return rows;
    }

    public static string Clip(string value, int width)
    {
        var rendered = new StringBuilder();
        var used = 0;
        foreach (var grapheme in EnumerateGraphemes(value))
        {
            var graphemeWidth = WidthGrapheme(grapheme);
            if (used + graphemeWidth > Math.Max(0, width))
            {
                break;
            }

            _ = rendered.Append(grapheme);
            used += graphemeWidth;
        }

        return rendered.ToString();
    }

    public static int Width(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark ||
            rune.Value is 0x200d or >= 0x1f3fb and <= 0x1f3ff or >= 0xfe00 and <= 0xfe0f or >= 0xe0100 and <= 0xe01ef)
        {
            return 0;
        }

        var value = rune.Value;
        return value is >= 0x1100 and <= 0x115f
            or 0x2329
            or 0x232a
            or >= 0x2e80 and <= 0xa4cf
            or >= 0xac00 and <= 0xd7a3
            or >= 0xf900 and <= 0xfaff
            or >= 0xfe10 and <= 0xfe6f
            or >= 0xff00 and <= 0xff60
            or >= 0x1f300 and <= 0x1faff
            or >= 0x20000 and <= 0x3fffd
                ? 2
                : 1;
    }

    public static int Width(string value)
    {
        var width = 0;
        foreach (var grapheme in EnumerateGraphemes(value))
        {
            width += WidthGrapheme(grapheme);
        }

        return width;
    }

    public static IEnumerable<string> EnumerateGraphemes(string value)
    {
        var elements = StringInfo.GetTextElementEnumerator(value);
        while (elements.MoveNext())
        {
            yield return (string)elements.Current;
        }
    }

    private static bool IsWordBreak(string grapheme)
    {
        foreach (var rune in grapheme.EnumerateRunes())
        {
            return Rune.IsWhiteSpace(rune);
        }

        return false;
    }

    private static int WidthGrapheme(string grapheme)
    {
        var width = 0;
        var maximumRuneWidth = 0;
        var regionalIndicators = 0;
        var runeCount = 0;
        var hasJoiner = false;

        foreach (var rune in grapheme.EnumerateRunes())
        {
            runeCount++;
            hasJoiner |= rune.Value == 0x200d;
            regionalIndicators += rune.Value is >= 0x1f1e6 and <= 0x1f1ff ? 1 : 0;
            var runeWidth = Width(rune);
            width += runeWidth;
            maximumRuneWidth = Math.Max(maximumRuneWidth, runeWidth);
        }

        if (runeCount == 2 && regionalIndicators == 2)
        {
            return 2;
        }

        return hasJoiner ? maximumRuneWidth : width;
    }
}
