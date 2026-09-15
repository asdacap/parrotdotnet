namespace Parrot.Cli.Enhanced;

internal readonly record struct QueueLiveBufferItem(string Name, string Description, int ItemCount) : ILiveBufferItem
{
    public string OwnerAgentSessionId { get; init; } = string.Empty;

    public MultiLine Render(LiveBufferRenderContext context)
    {
        var columns = context.Decoration.ContentColumns(context.Columns);
        var name = Normalize(Name);
        var description = Normalize(Description);
        var count = ItemCount == 1 ? " · 1 item" : $" · {ItemCount} items";
        const string prefix = "queue: ";
        var required = prefix + name + count;
        var detail = description.Length == 0 ? string.Empty : $" — {description}";
        var requiredWidth = TerminalText.Width(required);
        var reservedWidth = TerminalText.Width(prefix) + TerminalText.Width(count);
        var text = columns >= requiredWidth
            ? required + TerminalText.Clip(detail, columns - requiredWidth)
            : columns >= reservedWidth
                ? prefix + TerminalText.Clip(name, columns - reservedWidth) + count
                : TerminalText.Clip(required + detail, columns);
        return new MultiLine(
            [.. context.Decoration.Apply(string.Empty, [text])
                .Select(line => new TerminalLine(line, context.Palette.LiveMuted))],
            null,
            LiveBufferRetention.Fixed);
    }

    private static string Normalize(string value) =>
        TerminalText.Sanitize(value).Replace("\n", " ", StringComparison.Ordinal).Trim();
}
