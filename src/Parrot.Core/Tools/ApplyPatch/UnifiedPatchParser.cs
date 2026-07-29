using System.Globalization;
using System.Text;

namespace Parrot.Tools.ApplyPatch;

internal static class UnifiedPatchParser
{
    public static Patch Parse(string text)
    {
        var lines = PatchText.Lines(text);
        RejectUnsupportedForms(lines);
        var operations = new List<PatchOperation>();

        for (var index = 0; index < lines.Length;)
        {
            if (!lines[index].StartsWith("--- ", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            if (index + 1 >= lines.Length || !lines[index + 1].StartsWith("+++ ", StringComparison.Ordinal))
            {
                throw new PatchException($"Invalid unified patch at line {index + 1}: missing target header.");
            }

            var source = ReadHeaderPath(lines[index][4..]);
            var target = ReadHeaderPath(lines[index + 1][4..]);

            if (source.Length > 0 && target.Length > 0
                && !string.Equals(source, target, StringComparison.Ordinal))
            {
                throw new PatchException($"Source '{source}' and target '{target}' differ; renames are not supported.");
            }

            index += 2;
            var hunks = new List<PatchHunk>();

            while (index < lines.Length && !lines[index].StartsWith("--- ", StringComparison.Ordinal))
            {
                if (lines[index].StartsWith("diff --git ", StringComparison.Ordinal))
                {
                    break;
                }

                if (!lines[index].StartsWith("@@ ", StringComparison.Ordinal))
                {
                    index++;
                    continue;
                }

                hunks.Add(ReadHunk(lines, ref index));
            }

            operations.Add(BuildOperation(source, target, hunks));
        }

        if (operations.Count == 0)
        {
            throw new PatchException("Patch has no file headers.");
        }

        return Patch.Validate(operations);
    }

    private static PatchHunk ReadHunk(string[] lines, ref int index)
    {
        var headerLine = index + 1;
        var (expectedOldCount, expectedNewCount) = ReadCounts(lines[index]);
        var hunkLines = new List<PatchLine>();
        var oldCount = 0;
        var newCount = 0;
        index++;

        while (index < lines.Length)
        {
            var line = lines[index];

            if (line.StartsWith("@@ ", StringComparison.Ordinal)
                || line.StartsWith("--- ", StringComparison.Ordinal)
                || line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                break;
            }

            if (string.Equals(line, "\\ No newline at end of file", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            if (line.Length == 0 || line[0] is not (' ' or '-' or '+'))
            {
                throw new PatchException($"Invalid unified patch at line {index + 1}: unprefixed hunk line.");
            }

            var kind = line[0];
            hunkLines.Add(new PatchLine(kind, line[1..]));

            if (kind != '+')
            {
                oldCount++;
            }

            if (kind != '-')
            {
                newCount++;
            }

            index++;
        }

        if (hunkLines.Count == 0)
        {
            throw new PatchException($"Invalid unified patch at line {headerLine}: hunk has no lines.");
        }

        if (oldCount != expectedOldCount || newCount != expectedNewCount)
        {
            throw new PatchException($"Invalid unified patch at line {headerLine}: hunk line counts do not match.");
        }

        return new PatchHunk(hunkLines, new UnifiedPatchMatchPolicy());
    }

    private static (int Old, int New) ReadCounts(string header)
    {
        var closing = header.IndexOf(" @@", StringComparison.Ordinal);

        if (closing < 4)
        {
            throw new PatchException($"Malformed hunk header '{header}'.");
        }

        var ranges = header[3..closing].Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (ranges.Length != 2 || ranges[0][0] != '-' || ranges[1][0] != '+')
        {
            throw new PatchException($"Malformed hunk header '{header}'.");
        }

        return (ReadCount(ranges[0][1..], header), ReadCount(ranges[1][1..], header));
    }

    private static int ReadCount(string range, string header)
    {
        var parts = range.Split(',');
        var count = 0;

        if (parts.Length is < 1 or > 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out _)
            || (parts.Length == 2
                && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out count)))
        {
            throw new PatchException($"Malformed hunk header '{header}'.");
        }

        return parts.Length == 1 ? 1 : count;
    }

    private static PatchOperation BuildOperation(
        string source,
        string target,
        IReadOnlyList<PatchHunk> hunks)
    {
        if (source.Length == 0)
        {
            var data = new StringBuilder();

            foreach (var line in hunks.SelectMany(hunk => hunk.Lines))
            {
                if (line.Kind != '+')
                {
                    throw new PatchException($"File creation for '{target}' may only add lines.");
                }

                _ = data.Append(line.Text).Append('\n');
            }

            return new PatchOperation(PatchOperationKind.Add, target, data.ToString(), []);
        }

        return target.Length == 0
            ? new PatchOperation(PatchOperationKind.Delete, source, string.Empty, [])
            : new PatchOperation(PatchOperationKind.Update, target, string.Empty, hunks);
    }

    private static string ReadHeaderPath(string value)
    {
        var path = PatchText.HeaderPath(value);

        if (path.StartsWith('"'))
        {
            throw new PatchException("Quoted paths are not supported.");
        }

        if (path.Length > 0)
        {
            Patch.ValidatePath(path);
        }

        return path;
    }

    private static void RejectUnsupportedForms(string[] lines)
    {
        foreach (var line in lines)
        {
            if (line.StartsWith("rename from ", StringComparison.Ordinal)
                || line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                throw new PatchException("Renames are not supported; express the change as a delete and a create.");
            }

            if (line.StartsWith("copy from ", StringComparison.Ordinal)
                || line.StartsWith("copy to ", StringComparison.Ordinal))
            {
                throw new PatchException("Copies are not supported; express the change as a create.");
            }

            if (string.Equals(line, "GIT binary patch", StringComparison.Ordinal)
                || line.StartsWith("Binary files ", StringComparison.Ordinal))
            {
                throw new PatchException("Binary patches are not supported.");
            }
        }
    }
}
