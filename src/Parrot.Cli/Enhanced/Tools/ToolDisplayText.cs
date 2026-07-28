using System.Text;

namespace Parrot.Cli.Enhanced.Tools;

internal static class ToolDisplayText
{
    private const int MaximumDetailBytes = 16 * 1024;
    private const int MaximumDetailLines = 10;
    private const int MaximumLabelBytes = 1024;
    private const string LabelTruncated = "…";
    private const string Truncated = "… display truncated";
    private static readonly int LabelTruncatedBytes = Encoding.UTF8.GetByteCount(LabelTruncated);
    private static readonly int TruncatedBytes = Encoding.UTF8.GetByteCount(Truncated);

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

    public static IReadOnlyList<string> Details(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var lines = new List<string>(MaximumDetailLines);
        var line = new StringBuilder();
        var bytes = 0;
        var truncated = false;
        foreach (var value in values)
        {
            foreach (var rune in value.EnumerateRunes())
            {
                if (lines.Count == MaximumDetailLines)
                {
                    truncated = true;
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
                        break;
                    }

                    _ = line.Append(rune);
                    bytes += rune.Utf8SequenceLength;
                }
            }

            if (truncated)
            {
                break;
            }

            if (lines.Count == MaximumDetailLines)
            {
                truncated = true;
                break;
            }

            lines.Add(line.ToString());
            _ = line.Clear();
        }

        if (truncated)
        {
            if (line.Length > 0 && lines.Count < MaximumDetailLines)
            {
                lines.Add(line.ToString());
            }

            while (lines.Count >= MaximumDetailLines)
            {
                bytes -= Encoding.UTF8.GetByteCount(lines[^1]);
                lines.RemoveAt(lines.Count - 1);
            }

            while (bytes + TruncatedBytes > MaximumDetailBytes)
            {
                var last = lines[^1];
                var lastBytes = Encoding.UTF8.GetByteCount(last);
                var allowed = MaximumDetailBytes - TruncatedBytes - (bytes - lastBytes);
                if (allowed > 0)
                {
                    lines[^1] = TruncateUtf8(last, allowed);
                    break;
                }

                bytes -= lastBytes;
                lines.RemoveAt(lines.Count - 1);
            }

            lines.Add(Truncated);
        }

        return lines;
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
