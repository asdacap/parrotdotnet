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
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value == '\n')
            {
                rows.Add(row.ToString());
                _ = row.Clear().Append(indent);
                cells = Width(indent);
                continue;
            }

            var runeWidth = Width(rune);
            if (cells > 0 && cells + runeWidth > width)
            {
                rows.Add(row.ToString());
                _ = row.Clear().Append(indent);
                cells = Width(indent);
            }

            _ = row.Append(rune);
            cells += runeWidth;
        }

        rows.Add(row.ToString());
        return rows;
    }

    public static string Clip(string value, int width)
    {
        var rendered = new StringBuilder();
        var used = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var runeWidth = Width(rune);
            if (used + runeWidth > Math.Max(0, width))
            {
                break;
            }

            _ = rendered.Append(rune);
            used += runeWidth;
        }

        return rendered.ToString();
    }

    public static int Width(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
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
        foreach (var rune in value.EnumerateRunes())
        {
            width += Width(rune);
        }

        return width;
    }
}
