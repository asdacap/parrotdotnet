namespace Parrot.Cli.Enhanced.Tools;

internal readonly record struct ToolBlock(
    ToolBlockKind Kind,
    string Text)
{
    public static ToolBlock Empty { get; } = new(ToolBlockKind.None, string.Empty);

    public static ToolBlock FromText(string text) =>
        text.Length == 0 ? Empty : new(ToolBlockKind.Text, text);

    public static ToolBlock FromStatus(string text) =>
        text.Length == 0 ? Empty : new(ToolBlockKind.Status, text);

    public static ToolBlock FromOutput(string text) =>
        text.Length == 0 ? Empty : new(ToolBlockKind.Output, text);

    public static ToolBlock FromDetails(IEnumerable<string> details)
    {
        var values = details.Where(value => value.Length > 0).ToArray();
        return values.Length == 0 ? Empty : FromText(string.Join('\n', values));
    }

    public static ToolBlock FromDiff(string text) =>
        new(ToolBlockKind.Diff, text);

    public static ToolBlock FromQueue(IEnumerable<string> items) =>
        FromQueue(string.Join('\n', items));

    public static ToolBlock FromQueue(string text) =>
        text.Length == 0 ? Empty : new(ToolBlockKind.Queue, text);

    public static ToolBlock FromCompletedInput(string text) =>
        new(ToolBlockKind.CompletedInput, text);

    public static ToolBlock FromError(string text) =>
        text.Length == 0 ? Empty : new(ToolBlockKind.Error, text);
}
