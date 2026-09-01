using System.Globalization;
using System.Text;

namespace Parrot.Cli.Enhanced;

internal static class HierarchicalActivityValue
{
    private const string SpinnerFrames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";

    public static Decoration Describe(int columns, int depth, string? label, string adornment)
    {
        var indentation = new string(' ', Math.Max(0, depth) * 2);
        var cleanLabel = SanitizeSingleLine(label);
        var cleanAdornment = SanitizeSingleLine(adornment);
        var hierarchical = indentation.Length > 0 || cleanLabel.Length > 0 || cleanAdornment.Length > 0;
        if (!hierarchical)
        {
            return new Decoration(string.Empty, string.Empty, string.Empty, false);
        }

        var availableDecorationWidth = Math.Max(0, columns - 1);
        var maximumIndentationWidth = Math.Max(0, availableDecorationWidth - 2);
        indentation = TerminalText.Clip(indentation, maximumIndentationWidth);
        var fixedWidth = TerminalText.Width(indentation) + 2;
        var remainingWidth = Math.Max(0, availableDecorationWidth - fixedWidth);
        var labelField = cleanLabel.Length == 0 ? string.Empty : $"[{cleanLabel}] ";
        labelField = TerminalText.Clip(labelField, remainingWidth);
        remainingWidth -= TerminalText.Width(labelField);
        var adornmentField = cleanAdornment.Length == 0 ? string.Empty : $"{cleanAdornment} ";
        adornmentField = TerminalText.Clip(adornmentField, remainingWidth);
        return new Decoration(indentation, labelField, adornmentField, true);
    }

    public static DecoratedLine Decorate(
        string value,
        Decoration decoration,
        string? successfulIcon,
        bool first,
        int columns)
    {
        if (TryUnwrapStyle(value, out var style, out var styledContent))
        {
            var decorated = Decorate(styledContent, decoration, successfulIcon, first, columns);
            return decorated with { Text = style + decorated.Text + TerminalStyle.Reset };
        }

        string marker;
        string content;
        if (first)
        {
            var (icon, innerContent) = SplitIcon(value, successfulIcon);
            marker = icon + " ";
            content = innerContent;
        }
        else
        {
            marker = decoration.Hierarchical ? "  " : string.Empty;
            content = TrimDetail(value);
        }

        var adornmentField = first ? decoration.AdornmentField : new string(' ', decoration.AdornmentWidth);
        var prefix = decoration.Indentation + marker + decoration.LabelField + adornmentField;
        var contentWidth = Math.Max(0, columns - TerminalText.Width(prefix));
        return new DecoratedLine(
            (prefix + ClipStyled(content, contentWidth)).TrimEnd(),
            TerminalText.Width(prefix),
            first ? decoration.GlyphStartCell : -1,
            first ? TerminalText.Width(decoration.AdornmentField.TrimEnd()) : 0);
    }

    public static string RemoveOwner(string value, string owner)
    {
        var sanitizedOwner = SanitizeSingleLine(owner);
        if (sanitizedOwner.Length == 0 || !value.StartsWith(sanitizedOwner, StringComparison.Ordinal))
        {
            return value;
        }

        var remaining = value[sanitizedOwner.Length..];
        return remaining.StartsWith(": ", StringComparison.Ordinal) ? remaining[2..] : value;
    }

    private static string ClipStyled(string value, int width)
    {
        var rendered = new StringBuilder(value.Length);
        var cells = 0;
        var position = 0;
        while (position < value.Length)
        {
            if (value[position] == '\u001b' && TryReadStyle(value, position, out var styleLength))
            {
                _ = rendered.Append(value, position, styleLength);
                position += styleLength;
                continue;
            }

            var grapheme = StringInfo.GetNextTextElement(value, position);
            var graphemeWidth = TerminalText.Width(grapheme);
            if (cells + graphemeWidth > Math.Max(0, width))
            {
                break;
            }

            _ = rendered.Append(grapheme);
            cells += graphemeWidth;
            position += grapheme.Length;
        }

        return rendered.ToString();
    }

    private static bool TryReadStyle(string value, int position, out int length)
    {
        length = 0;
        if (position + 2 >= value.Length || value[position + 1] != '[')
        {
            return false;
        }

        var terminator = value.IndexOf('m', position + 2);
        if (terminator < 0)
        {
            return false;
        }

        for (var index = position + 2; index < terminator; index++)
        {
            if (value[index] is not (';' or >= '0' and <= '9'))
            {
                return false;
            }
        }

        length = terminator - position + 1;
        return true;
    }

    private static string SanitizeSingleLine(string? value) => value is null
        ? string.Empty
        : TerminalText.Sanitize(value).Replace("\n", string.Empty, StringComparison.Ordinal);

    private static string TrimDetail(string value) => value.StartsWith("  ", StringComparison.Ordinal)
        ? value[2..]
        : value;

    private static bool TryUnwrapStyle(string value, out string style, out string content)
    {
        if (value.StartsWith("\u001b[", StringComparison.Ordinal)
            && value.IndexOf('m', StringComparison.Ordinal) is var styleEnd
            && styleEnd >= 0
            && value.EndsWith(TerminalStyle.Reset, StringComparison.Ordinal))
        {
            style = value[..(styleEnd + 1)];
            content = value[(styleEnd + 1)..^TerminalStyle.Reset.Length];
            return true;
        }

        style = string.Empty;
        content = string.Empty;
        return false;
    }

    private static (string Icon, string Content) SplitIcon(string value, string? successfulIcon)
    {
        foreach (var icon in new[] { "●", "$", "○", "◐", "✓", "✗", "■", "♟", "✦", "•" })
        {
            if (value.StartsWith(icon + " ", StringComparison.Ordinal))
            {
                return (icon, value[(icon.Length + 1)..]);
            }
        }

        if (value.Length == 1 && "●$○◐✓✗■♟✦•".Contains(value, StringComparison.Ordinal))
        {
            return (value, string.Empty);
        }

        if (value.Length >= 2 && value[1] == ' ')
        {
            var marker = value[..1];
            if (SpinnerFrames.Contains(marker, StringComparison.Ordinal))
            {
                return (marker, value[2..]);
            }

            return marker switch
            {
                "+" => (successfulIcon ?? "✓", value[2..]),
                "-" => ("■", value[2..]),
                "!" => ("✗", value[2..]),
                "*" => ("◐", value[2..]),
                _ => ("•", value),
            };
        }

        return ("•", value);
    }

    internal readonly record struct Decoration(
        string Indentation,
        string LabelField,
        string AdornmentField,
        bool Hierarchical)
    {
        public int Width => TerminalText.Width(Indentation) + (Hierarchical ? 2 : 0) +
            TerminalText.Width(LabelField) + AdornmentWidth;

        public int AdornmentWidth => TerminalText.Width(AdornmentField);

        public int GlyphStartCell => TerminalText.Width(Indentation) + (Hierarchical ? 2 : 0) +
            TerminalText.Width(LabelField);
    }

    internal readonly record struct DecoratedLine(string Text, int PrefixWidth, int GlyphStartCell, int GlyphWidth);
}
