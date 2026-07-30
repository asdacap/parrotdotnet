namespace Parrot.Cli.Enhanced;

internal static class HierarchicalActivityValue
{
    private const string SpinnerFrames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";

    public static string Format(
        string value,
        int depth,
        string? label,
        string owner,
        string? successfulIcon,
        string adornment,
        bool first)
    {
        if (value.StartsWith("\u001b[", StringComparison.Ordinal)
            && value.IndexOf('m', StringComparison.Ordinal) is var styleEnd
            && styleEnd >= 0
            && value.EndsWith(TerminalStyle.Reset, StringComparison.Ordinal))
        {
            var style = value[..(styleEnd + 1)];
            var styledContent = value[(styleEnd + 1)..^TerminalStyle.Reset.Length];
            return style + Format(styledContent, depth, label, owner, successfulIcon, adornment, first) +
                TerminalStyle.Reset;
        }

        var indentation = new string(' ', Math.Max(0, depth) * 2);
        var agent = label is null ? string.Empty : $"[{TerminalText.Sanitize(label)}] ";
        if (!first)
        {
            var contentIndentation = label is null ? string.Empty : "  ";
            return indentation + contentIndentation + agent + TrimDetail(value);
        }

        var (icon, content) = SplitIcon(value, successfulIcon);
        content = TrimOwner(content, owner);
        var cleanAdornment = TerminalText.Sanitize(adornment).Replace("\n", string.Empty, StringComparison.Ordinal);
        var decoratedAgent = cleanAdornment.Length == 0 ? agent : $"{agent}{cleanAdornment} ";
        return $"{indentation}{icon} {decoratedAgent}{content}".TrimEnd();
    }

    private static (string Icon, string Content) SplitIcon(string value, string? successfulIcon)
    {
        foreach (var icon in new[] { "●", "$", "○", "◐", "✓", "✗", "■", "♟", "•" })
        {
            if (value.StartsWith(icon + " ", StringComparison.Ordinal))
            {
                return (icon, value[(icon.Length + 1)..]);
            }
        }

        if (value.Length < 2 || value[1] != ' ')
        {
            return ("•", value);
        }

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

    private static string TrimOwner(string value, string owner)
    {
        var sanitized = TerminalText.Sanitize(owner);
        if (sanitized.Length == 0 || !value.StartsWith(sanitized, StringComparison.Ordinal))
        {
            return value;
        }

        var remaining = value[sanitized.Length..];
        return remaining.StartsWith(": ", StringComparison.Ordinal) ? remaining[2..] : value;
    }

    private static string TrimDetail(string value) => value.StartsWith("  ", StringComparison.Ordinal)
        ? value[2..]
        : value;
}
