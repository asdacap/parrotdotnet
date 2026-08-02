using System.Text;

namespace Parrot.Cli.Enhanced.Tools;

internal static class ToolDisplayText
{
    private const int MaximumDetailBytes = 16 * 1024;
    private const int MaximumDetailLines = 10;
    private const int MaximumLabelBytes = 1024;
    private const string LabelTruncated = "…";
    private static readonly int LabelTruncatedBytes = Encoding.UTF8.GetByteCount(LabelTruncated);

    public static string Label(string value)
    {
        var newline = value.IndexOf('\n', StringComparison.Ordinal);
        var firstLineLength = newline < 0 ? value.Length : newline;
        var boundedLength = Math.Min(firstLineLength, MaximumLabelBytes);
        if (boundedLength < firstLineLength && char.IsHighSurrogate(value[boundedLength - 1]))
        {
            boundedLength--;
        }

        var bounded = TruncateUtf8(value[..boundedLength], MaximumLabelBytes, out var bytesTruncated);
        var truncated = firstLineLength > boundedLength || bytesTruncated;
        var label = TerminalText.Sanitize(bounded);
        if (Encoding.UTF8.GetByteCount(label) <= MaximumLabelBytes && !truncated)
        {
            return label;
        }

        return TruncateUtf8(label, MaximumLabelBytes - LabelTruncatedBytes, out _) + LabelTruncated;
    }

    public static IReadOnlyList<string> LayoutDetails(
        IEnumerable<string> details,
        int columns,
        int maximumLines) => LayoutDetails(details, columns, maximumLines, MaximumDetailLines);

    public static IReadOnlyList<string> LayoutDetails(
        IEnumerable<string> details,
        int columns,
        int maximumLines,
        int maximumDetailLines)
    {
        var rendered = Details(details, maximumDetailLines)
            .SelectMany(detail => TerminalText.Layout($"  {detail}", columns))
            .ToList();
        if (rendered.Count <= maximumLines)
        {
            return rendered;
        }

        var bounded = rendered.Take(Math.Max(0, maximumLines - 1)).ToList();
        if (maximumLines > 0)
        {
            var truncated = rendered.Count - bounded.Count;
            bounded.AddRange(TerminalText.Layout($"  .. {truncated} lines truncated.", columns).Take(1));
        }

        return bounded;
    }

    public static IReadOnlyList<string> Details(IEnumerable<string> values) => Details(values, MaximumDetailLines);

    public static IReadOnlyList<string> Details(IEnumerable<string> values, int maximumLines)
    {
        ArgumentNullException.ThrowIfNull(values);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLines);

        var lines = new List<string>(maximumLines);
        var line = new StringBuilder();
        var bytes = 0;
        var truncated = false;
        var truncatedLines = 0;
        using var enumerator = values.GetEnumerator();
        while (enumerator.MoveNext())
        {
            var value = enumerator.Current;
            var offset = 0;
            foreach (var rune in value.EnumerateRunes())
            {
                if (lines.Count == maximumLines)
                {
                    truncated = true;
                    truncatedLines = CountLines(value.AsSpan(offset)) + CountRemainingLines(enumerator);
                    break;
                }

                if (rune.Value == '\n')
                {
                    lines.Add(line.ToString());
                    _ = line.Clear();
                    continue;
                }

                if (rune.Value == '\t')
                {
                    if (bytes + 4 > MaximumDetailBytes)
                    {
                        truncated = true;
                        truncatedLines = CountLines(value.AsSpan(offset)) + CountRemainingLines(enumerator);
                        break;
                    }

                    _ = line.Append("    ");
                    bytes += 4;
                }
                else if (!Rune.IsControl(rune))
                {
                    if (bytes + rune.Utf8SequenceLength > MaximumDetailBytes)
                    {
                        truncated = true;
                        truncatedLines = CountLines(value.AsSpan(offset)) + CountRemainingLines(enumerator);
                        break;
                    }

                    _ = line.Append(rune);
                    bytes += rune.Utf8SequenceLength;
                }

                offset += rune.Utf16SequenceLength;
            }

            if (truncated)
            {
                break;
            }

            if (lines.Count == maximumLines)
            {
                truncated = true;
                truncatedLines = CountRemainingLines(enumerator);
                break;
            }

            lines.Add(line.ToString());
            _ = line.Clear();
        }

        if (truncated)
        {
            if (line.Length > 0 && lines.Count < maximumLines)
            {
                lines.Add(line.ToString());
            }

            while (lines.Count >= maximumLines)
            {
                bytes -= Encoding.UTF8.GetByteCount(lines[^1]);
                lines.RemoveAt(lines.Count - 1);
                truncatedLines++;
            }

            var truncation = $".. {truncatedLines} lines truncated.";
            var truncationBytes = Encoding.UTF8.GetByteCount(truncation);
            while (bytes + truncationBytes > MaximumDetailBytes)
            {
                var last = lines[^1];
                var lastBytes = Encoding.UTF8.GetByteCount(last);
                var allowed = MaximumDetailBytes - truncationBytes - (bytes - lastBytes);
                if (allowed > 0)
                {
                    lines[^1] = TruncateUtf8(last, allowed);
                    break;
                }

                bytes -= lastBytes;
                lines.RemoveAt(lines.Count - 1);
            }

            lines.Add(truncation);
        }

        return lines;
    }

    private static int CountRemainingLines(IEnumerator<string> values)
    {
        var count = 0;
        while (values.MoveNext())
        {
            count += CountLines(values.Current);
        }

        return count;
    }

    private static int CountLines(ReadOnlySpan<char> value)
    {
        var count = 1;
        foreach (var character in value)
        {
            if (character == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static string TruncateUtf8(string value, int maximumBytes) =>
        TruncateUtf8(value, maximumBytes, out _);

    private static string TruncateUtf8(string value, int maximumBytes, out bool truncated)
    {
        if (maximumBytes <= 0)
        {
            truncated = value.Length > 0;
            return string.Empty;
        }

        var result = new StringBuilder(Math.Min(value.Length, maximumBytes));
        var bytes = 0;
        var characters = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (bytes + runeBytes > maximumBytes)
            {
                break;
            }

            _ = result.Append(rune);
            bytes += runeBytes;
            characters += rune.Utf16SequenceLength;
        }

        truncated = characters < value.Length;
        return truncated ? result.ToString() : value;
    }
}
