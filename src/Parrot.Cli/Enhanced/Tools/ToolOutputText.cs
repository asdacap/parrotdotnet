namespace Parrot.Cli.Enhanced.Tools;

internal static class ToolOutputText
{
    public static string Tail(string value, int maximumLines)
    {
        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n')
            .Split('\n');
        return string.Join('\n', lines.TakeLast(maximumLines));
    }

    public static int CountLines(string value) => value.Split('\n')
        .Count(line => line.Trim() is { Length: > 0 } trimmed && trimmed[0] != '[');
}
