namespace Parrot.Cli.Enhanced.Tools;

internal readonly record struct ToolBlock(
    ToolBlockKind Kind,
    string Text,
    string Language,
    string Path,
    int Line)
{
    public static ToolBlock Empty { get; } = new(ToolBlockKind.None, string.Empty, string.Empty, string.Empty, 0);

    public static ToolBlock FromText(string text) =>
        text.Length == 0 ? Empty : new(ToolBlockKind.Text, text, string.Empty, string.Empty, 0);

    public static ToolBlock FromStatus(string text) =>
        text.Length == 0 ? Empty : new(ToolBlockKind.Status, text, string.Empty, string.Empty, 0);

    public static ToolBlock FromOutput(string text) =>
        text.Length == 0 ? Empty : new(ToolBlockKind.Output, text, string.Empty, string.Empty, 0);

    public static ToolBlock FromDetails(IEnumerable<string> details)
    {
        var values = details.Where(value => value.Length > 0).ToArray();
        return values.Length == 0 ? Empty : FromText(string.Join('\n', values));
    }

    public static ToolBlock FromDiff(string text) =>
        new(ToolBlockKind.Diff, text, string.Empty, string.Empty, 0);

    public static ToolBlock FromCode(string text, string language, string path, int line) =>
        new(ToolBlockKind.Code, text, language, path, line);

    public static ToolBlock FromQueue(IEnumerable<string> items) =>
        FromQueue(string.Join('\n', items));

    public static ToolBlock FromQueue(string text) =>
        text.Length == 0 ? Empty : new(ToolBlockKind.Queue, text, string.Empty, string.Empty, 0);

    public static ToolBlock FromCompletedInput(string text) =>
        new(ToolBlockKind.CompletedInput, text, "yaml", string.Empty, 0);

    public static ToolBlock FromError(string text) =>
        text.Length == 0 ? Empty : new(ToolBlockKind.Error, text, string.Empty, string.Empty, 0);
}
