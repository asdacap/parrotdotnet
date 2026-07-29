using System.Text;

namespace Parrot.Tools.ApplyPatch;

internal static class PatchApplicator
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Apply(byte[] data, IReadOnlyList<PatchHunk> hunks)
    {
        var bom = data.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var lines = ReadLines(bom ? data[Encoding.UTF8.Preamble.Length..] : data);
        var replacements = new List<PatchReplacement>();
        var start = 0;
        var failures = new List<string>();

        for (var hunkIndex = 0; hunkIndex < hunks.Count; hunkIndex++)
        {
            var hunk = hunks[hunkIndex];
            var oldLines = hunk.Lines.Where(line => line.Kind != '+').Select(line => line.Text).ToArray();
            var newLines = hunk.Lines.Where(line => line.Kind != '-').Select(line => line.Text).ToArray();
            try
            {
                var found = oldLines.Length == 0 ? lines.Count : Find(lines, oldLines, start);
                var ending = SelectEnding(lines, found, oldLines.Length);
                replacements.Add(new PatchReplacement(found, oldLines.Length, BuildLines(newLines, ending, found + oldLines.Length == lines.Count && oldLines.Length > 0 && lines[found + oldLines.Length - 1].Ending.Length == 0)) { Order = hunkIndex });
                start = found + oldLines.Length;
            }
            catch (PatchException failure)
            {
                failures.Add($"hunk {hunkIndex + 1}: {failure.Message}");
            }
        }

        if (failures.Count > 0)
        {
            throw new PatchException(string.Join("; ", failures));
        }

        foreach (var replacement in replacements.OrderByDescending(item => item.Start).ThenByDescending(item => item.Order))
        {
            lines.RemoveRange(replacement.Start, replacement.OldCount);
            lines.InsertRange(replacement.Start, replacement.Lines);
        }

        var output = new byte[lines.Sum(line => line.Content.Length + line.Ending.Length)];
        var offset = 0;
        foreach (var line in lines)
        {
            line.Content.CopyTo(output, offset);
            offset += line.Content.Length;
            line.Ending.CopyTo(output, offset);
            offset += line.Ending.Length;
        }

        if (!bom)
        {
            return output;
        }

        var result = new byte[Encoding.UTF8.Preamble.Length + output.Length];
        Encoding.UTF8.Preamble.CopyTo(result.AsSpan());
        output.CopyTo(result, Encoding.UTF8.Preamble.Length);
        return result;
    }

    private static List<PatchFileLine> ReadLines(byte[] data)
    {
        var lines = new List<PatchFileLine>();
        var start = 0;
        for (var index = 0; index < data.Length; index++)
        {
            if (data[index] is not ((byte)'\n' or (byte)'\r'))
            {
                continue;
            }

            var ending = data[index] == (byte)'\r' && index + 1 < data.Length && data[index + 1] == (byte)'\n'
                ? "\r\n"u8.ToArray()
                : [data[index]];
            lines.Add(new PatchFileLine(data[start..index], ending));
            start = index + ending.Length;
            index += ending.Length - 1;
        }

        if (start < data.Length)
        {
            lines.Add(new PatchFileLine(data[start..], []));
        }

        return lines;
    }

    private static int Find(List<PatchFileLine> lines, string[] expected, int start)
    {
        Func<string, string, bool>[] comparisons =
        [
            static (left, right) => string.Equals(left, right, StringComparison.Ordinal),
            static (left, right) => string.Equals(left.TrimEnd(' ', '\t'), right.TrimEnd(' ', '\t'), StringComparison.Ordinal),
            static (left, right) => string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal),
            static (left, right) => string.Equals(Normalize(left.Trim()), Normalize(right.Trim()), StringComparison.Ordinal),
        ];

        foreach (var equal in comparisons)
        {
            var matches = new List<int>();
            for (var index = start; index <= lines.Count - expected.Length; index++)
            {
                if (Matches(lines, expected, index, equal))
                {
                    matches.Add(index);
                }
            }

            if (matches.Count == 1)
            {
                return matches[0];
            }

            if (matches.Count > 1)
            {
                throw new PatchException($"Found {matches.Count} matches for {Describe(expected)}; include more surrounding lines.");
            }
        }

        throw new PatchException($"Failed to find expected lines {Describe(expected)}.");
    }

    private static bool Matches(List<PatchFileLine> lines, string[] expected, int start, Func<string, string, bool> equal)
    {
        for (var index = 0; index < expected.Length; index++)
        {
            string actual;
            try
            {
                actual = StrictUtf8.GetString(lines[start + index].Content);
            }
            catch (DecoderFallbackException)
            {
                return false;
            }

            if (!equal(actual, expected[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] SelectEnding(List<PatchFileLine> lines, int start, int count)
    {
        if (count > 0 && lines[start].Ending.Length > 0)
        {
            return lines[start].Ending;
        }

        for (var index = Math.Min(start, lines.Count - 1); index >= 0; index--)
        {
            if (lines[index].Ending.Length > 0)
            {
                return lines[index].Ending;
            }
        }

        for (var index = start; index < lines.Count; index++)
        {
            if (lines[index].Ending.Length > 0)
            {
                return lines[index].Ending;
            }
        }

        return [(byte)'\n'];
    }

    private static List<PatchFileLine> BuildLines(string[] texts, byte[] ending, bool finalUnterminated)
    {
        var lines = new List<PatchFileLine>(texts.Length);
        for (var index = 0; index < texts.Length; index++)
        {
            lines.Add(new PatchFileLine(StrictUtf8.GetBytes(texts[index]), finalUnterminated && index == texts.Length - 1 ? [] : [.. ending]));
        }

        return lines;
    }

    private static string Describe(string[] lines)
    {
        var text = string.Join("\n", lines);
        const int limit = 8192;
        return text.Length <= limit ? $"'{text}'" : $"'{text[..limit]}' (... {text.Length - limit} characters omitted)";
    }

    private static string Normalize(string value) => value
        .Replace('‘', '\'').Replace('’', '\'').Replace('‚', '\'').Replace('‛', '\'')
        .Replace('“', '"').Replace('”', '"').Replace('„', '"').Replace('‟', '"')
        .Replace('‐', '-').Replace('‑', '-').Replace('‒', '-').Replace('–', '-').Replace('—', '-').Replace('―', '-')
        .Replace("…", "...", StringComparison.Ordinal).Replace(' ', ' ');
}
