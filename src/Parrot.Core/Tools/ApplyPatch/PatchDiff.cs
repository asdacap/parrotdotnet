using System.Text;

namespace Parrot.Tools.ApplyPatch;

internal static class PatchDiff
{
    private const int ContextLines = 3;
    private const int MaximumEditDistance = 1024;
    private static readonly UTF8Encoding DisplayUtf8 = new(false, false);

    public static string Render(IReadOnlyList<FileChange> changes)
    {
        var output = new StringBuilder();

        foreach (var change in changes)
        {
            RenderFile(output, change);
        }

        return output.ToString();
    }

    private static void RenderFile(StringBuilder output, FileChange change)
    {
        var beforePath = change.Before is null ? "/dev/null" : $"a/{change.Path}";
        var afterPath = change.After is null ? "/dev/null" : $"b/{change.Path}";
        _ = output.Append("--- ").Append(beforePath).Append('\n');
        _ = output.Append("+++ ").Append(afterPath).Append('\n');

        if (ContainsNul(change.Before) || ContainsNul(change.After))
        {
            _ = output.Append("Binary files differ\n");
            return;
        }

        WriteHunks(output, ShortestEditScript(Lines(change.Before), Lines(change.After)));
    }

    private static bool ContainsNul(byte[]? data) => data is not null && Array.IndexOf(data, (byte)0) >= 0;

    private static string[] Lines(byte[]? data)
    {
        if (data is null || data.Length == 0)
        {
            return [];
        }

        var text = DisplayUtf8.GetString(data)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = text.Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    private static List<DiffEdit> ShortestEditScript(string[] oldLines, string[] newLines)
    {
        var maximum = Math.Min(oldLines.Length + newLines.Length, MaximumEditDistance);
        var offset = maximum + 1;
        var furthest = new int[(2 * maximum) + 3];
        furthest[offset + 1] = 0;
        var trace = new List<int[]>(maximum + 1);

        for (var distance = 0; distance <= maximum; distance++)
        {
            trace.Add([.. furthest]);

            for (var diagonal = -distance; diagonal <= distance; diagonal += 2)
            {
                var index = offset + diagonal;
                int x;

                if (diagonal == -distance
                    || (diagonal != distance && furthest[index - 1] < furthest[index + 1]))
                {
                    x = furthest[index + 1];
                }
                else
                {
                    x = furthest[index - 1] + 1;
                }

                var y = x - diagonal;
                while (x >= 0 && y >= 0 && x < oldLines.Length && y < newLines.Length
                    && string.Equals(oldLines[x], newLines[y], StringComparison.Ordinal))
                {
                    x++;
                    y++;
                }

                furthest[index] = x;
                if (x >= oldLines.Length && y >= newLines.Length)
                {
                    return BacktrackEdits(oldLines, newLines, trace, distance, offset);
                }
            }
        }

        return FallbackEditScript(oldLines, newLines);
    }

    private static List<DiffEdit> FallbackEditScript(string[] oldLines, string[] newLines)
    {
        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length
            && string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix
            && string.Equals(
                oldLines[^(suffix + 1)],
                newLines[^(suffix + 1)],
                StringComparison.Ordinal))
        {
            suffix++;
        }

        var edits = new List<DiffEdit>(oldLines.Length + newLines.Length);
        for (var index = 0; index < prefix; index++)
        {
            edits.Add(new DiffEdit(' ', oldLines[index], index + 1, index + 1));
        }

        for (var index = prefix; index < oldLines.Length - suffix; index++)
        {
            edits.Add(new DiffEdit('-', oldLines[index], index + 1, prefix + 1));
        }

        for (var index = prefix; index < newLines.Length - suffix; index++)
        {
            edits.Add(new DiffEdit('+', newLines[index], oldLines.Length - suffix + 1, index + 1));
        }

        for (var index = 0; index < suffix; index++)
        {
            var oldIndex = oldLines.Length - suffix + index;
            var newIndex = newLines.Length - suffix + index;
            edits.Add(new DiffEdit(' ', oldLines[oldIndex], oldIndex + 1, newIndex + 1));
        }

        return edits;
    }

    private static List<DiffEdit> BacktrackEdits(
        string[] oldLines,
        string[] newLines,
        List<int[]> trace,
        int distance,
        int offset)
    {
        var x = oldLines.Length;
        var y = newLines.Length;
        var reversed = new List<DiffEdit>(x + y);

        for (var current = distance; current > 0; current--)
        {
            var furthest = trace[current];
            var diagonal = x - y;
            var previousDiagonal = diagonal - 1;
            if (diagonal == -current
                || (diagonal != current && furthest[offset + diagonal - 1] < furthest[offset + diagonal + 1]))
            {
                previousDiagonal = diagonal + 1;
            }

            var previousX = furthest[offset + previousDiagonal];
            var previousY = previousX - previousDiagonal;
            while (x > previousX && y > previousY)
            {
                x--;
                y--;
                reversed.Add(new DiffEdit(' ', oldLines[x], x + 1, y + 1));
            }

            if (x == previousX)
            {
                y--;
                reversed.Add(new DiffEdit('+', newLines[y], x + 1, y + 1));
            }
            else
            {
                x--;
                reversed.Add(new DiffEdit('-', oldLines[x], x + 1, y + 1));
            }
        }

        while (x > 0 && y > 0)
        {
            x--;
            y--;
            reversed.Add(new DiffEdit(' ', oldLines[x], x + 1, y + 1));
        }

        reversed.Reverse();
        return reversed;
    }

    private static void WriteHunks(StringBuilder output, IReadOnlyList<DiffEdit> edits)
    {
        for (var index = 0; index < edits.Count;)
        {
            while (index < edits.Count && edits[index].Kind == ' ')
            {
                index++;
            }

            if (index == edits.Count)
            {
                return;
            }

            var start = Math.Max(0, index - ContextLines);
            var lastChange = index;
            for (var next = index + 1; next < edits.Count; next++)
            {
                if (edits[next].Kind == ' ')
                {
                    continue;
                }

                if (next - lastChange - 1 > ContextLines * 2)
                {
                    break;
                }

                lastChange = next;
            }

            var end = Math.Min(edits.Count, lastChange + ContextLines + 1);
            var oldCount = 0;
            var newCount = 0;
            for (var current = start; current < end; current++)
            {
                if (edits[current].Kind != '+')
                {
                    oldCount++;
                }

                if (edits[current].Kind != '-')
                {
                    newCount++;
                }
            }

            var oldStart = edits[start].OldLine;
            var newStart = edits[start].NewLine;
            if (oldCount == 0)
            {
                oldStart--;
            }

            if (newCount == 0)
            {
                newStart--;
            }

            _ = output.Append("@@ -").Append(oldStart).Append(',').Append(oldCount)
                .Append(" +").Append(newStart).Append(',').Append(newCount).Append(" @@\n");
            for (var current = start; current < end; current++)
            {
                _ = output.Append(edits[current].Kind).Append(edits[current].Text).Append('\n');
            }

            index = end;
        }
    }

    internal readonly record struct FileChange(string Path, byte[]? Before, byte[]? After);

    private readonly record struct DiffEdit(char Kind, string Text, int OldLine, int NewLine);
}
