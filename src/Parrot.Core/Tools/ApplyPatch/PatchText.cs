namespace Parrot.Tools.ApplyPatch;

internal static class PatchText
{
    public static string[] Lines(string text)
    {
        if (text.Contains('\0', StringComparison.Ordinal))
        {
            throw new PatchException("Invalid patch: NUL byte.");
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var first = 0;
        var last = lines.Length;
        while (first < last && lines[first].Length == 0)
        {
            first++;
        }

        while (last > first && lines[last - 1].Length == 0)
        {
            last--;
        }

        return lines[first..last];
    }

    public static string HeaderPath(string value)
    {
        var tab = value.IndexOf('\t', StringComparison.Ordinal);

        if (tab >= 0)
        {
            value = value[..tab];
        }

        value = value.Trim();

        if (string.Equals(value, "/dev/null", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return value.StartsWith("a/", StringComparison.Ordinal) || value.StartsWith("b/", StringComparison.Ordinal)
            ? value[2..]
            : value;
    }
}
