namespace Parrot.Cli.Enhanced;

internal readonly record struct QueueLiveBufferItem(string Name, string Description, int ItemCount) : ILiveBufferItem
{
    public MultiLine Render(LiveBufferRenderContext context)
    {
        var name = Normalize(Name);
        var description = Normalize(Description);
        var label = description.Length == 0 ? name : description;
        var suffix = ItemCount == 1 ? " · 1 item" : $" · {ItemCount} items";
        const string prefix = "  queue: ";
        var reserved = TerminalText.Width(prefix) + TerminalText.Width(suffix);
        var text = context.Columns >= reserved
            ? prefix + TerminalText.Clip(label, context.Columns - reserved) + suffix
            : TerminalText.Clip(prefix + label + suffix, context.Columns);
        return new MultiLine(
            [new TerminalLine(text, context.Palette.LiveMuted)],
            null,
            LiveBufferRetention.Fixed);
    }

    private static string Normalize(string value) =>
        TerminalText.Sanitize(value).Replace("\n", " ", StringComparison.Ordinal).Trim();
}
