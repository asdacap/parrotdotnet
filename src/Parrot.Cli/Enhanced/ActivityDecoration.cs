namespace Parrot.Cli.Enhanced;

/// <summary>
/// The hierarchy prefix an activity item lays its lines under: indentation, an optional
/// <c>[label]</c> field and an optional adornment, all placed after a two-cell marker column.
/// </summary>
internal readonly record struct ActivityDecoration(string Indentation, string LabelField, string AdornmentField)
{
    private const int MarkerColumnWidth = 2;

    public static ActivityDecoration None { get; } = new(string.Empty, string.Empty, string.Empty);

    public int Width => TerminalText.Width(Indentation) + MarkerColumnWidth + TerminalText.Width(LabelField) +
        TerminalText.Width(AdornmentField);

    public int GlyphStartCell => TerminalText.Width(Indentation) + MarkerColumnWidth + TerminalText.Width(LabelField);

    public int GlyphWidth => TerminalText.Width(AdornmentField.TrimEnd());

    public static ActivityDecoration Describe(int columns, int depth, string? label, string adornment)
    {
        var indentation = new string(' ', Math.Max(0, depth) * 2);
        var cleanLabel = SanitizeSingleLine(label);
        var cleanAdornment = SanitizeSingleLine(adornment);
        var availableDecorationWidth = Math.Max(0, columns - 1);
        var maximumIndentationWidth = Math.Max(0, availableDecorationWidth - MarkerColumnWidth);
        indentation = TerminalText.Clip(indentation, maximumIndentationWidth);
        var fixedWidth = TerminalText.Width(indentation) + MarkerColumnWidth;
        var remainingWidth = Math.Max(0, availableDecorationWidth - fixedWidth);
        var labelField = cleanLabel.Length == 0 ? string.Empty : $"[{cleanLabel}] ";
        labelField = TerminalText.Clip(labelField, remainingWidth);
        remainingWidth -= TerminalText.Width(labelField);
        var adornmentField = cleanAdornment.Length == 0 ? string.Empty : $"{cleanAdornment} ";
        adornmentField = TerminalText.Clip(adornmentField, remainingWidth);
        return new ActivityDecoration(indentation, labelField, adornmentField);
    }

    /// <summary>The columns left for content once the decoration is placed in front of each line.</summary>
    public int ContentColumns(int columns) => Math.Max(1, columns - Width);

    /// <summary>
    /// Places the single-cell <paramref name="marker"/> and the decoration in front of the first content
    /// line and a blank marker column with the decoration in front of every following line.
    /// </summary>
    public IReadOnlyList<string> Apply(string marker, IEnumerable<string> content)
    {
        var lead = Indentation + (marker.Length == 0 ? "  " : marker + " ") + LabelField + AdornmentField;
        var continuation = Indentation + "  " + LabelField + new string(' ', TerminalText.Width(AdornmentField));
        return [.. content.Select((line, index) => ((index == 0 ? lead : continuation) + line).TrimEnd())];
    }

    private static string SanitizeSingleLine(string? value) => value is null
        ? string.Empty
        : TerminalText.Sanitize(value).Replace("\n", string.Empty, StringComparison.Ordinal);
}
