namespace Parrot.Store;

internal sealed record HistoryForkSelection(HistoryForkKind Kind, string Title)
{
    public static HistoryForkSelection Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value switch
        {
            "" or "empty" => new HistoryForkSelection(HistoryForkKind.Empty, string.Empty),
            "full" => new HistoryForkSelection(HistoryForkKind.Full, string.Empty),
            _ => new HistoryForkSelection(HistoryForkKind.Named, value),
        };
    }
}
