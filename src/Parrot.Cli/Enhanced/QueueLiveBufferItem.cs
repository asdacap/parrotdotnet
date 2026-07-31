namespace Parrot.Cli.Enhanced;

internal readonly record struct QueueLiveBufferItem(string Name, string Description, int ItemCount) : ILiveBufferItem
{
    public string OwnerAgentSessionId { get; init; } = string.Empty;

    public MultiLine Render(LiveBufferRenderContext context)
    {
        var name = Normalize(Name);
        var description = Normalize(Description);
        var count = ItemCount == 1 ? " · 1 item" : $" · {ItemCount} items";
        const string prefix = "  queue: ";
        var required = prefix + name + count;
        var detail = description.Length == 0 ? string.Empty : $" — {description}";
        var requiredWidth = TerminalText.Width(required);
        var reservedWidth = TerminalText.Width(prefix) + TerminalText.Width(count);
        var text = context.Columns >= requiredWidth
            ? required + TerminalText.Clip(detail, context.Columns - requiredWidth)
            : context.Columns >= reservedWidth
                ? prefix + TerminalText.Clip(name, context.Columns - reservedWidth) + count
                : TerminalText.Clip(required + detail, context.Columns);
        return new MultiLine(
            [new TerminalLine(text, context.Palette.LiveMuted)],
            null,
            LiveBufferRetention.Fixed);
    }

    private static string Normalize(string value) =>
        TerminalText.Sanitize(value).Replace("\n", " ", StringComparison.Ordinal).Trim();
}
