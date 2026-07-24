namespace Parrot.Tools;

internal static class PatchText
{
    public static string[] Lines(string text)
    {
        if (text.Contains('\0', StringComparison.Ordinal))
        {
            throw new PatchException("Invalid patch: NUL byte.");
        }

        return [.. text.Trim().Split('\n').Select(line => line.TrimEnd('\r'))];
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
