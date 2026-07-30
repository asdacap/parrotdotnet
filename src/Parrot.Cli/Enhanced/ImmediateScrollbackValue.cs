namespace Parrot.Cli.Enhanced;

internal sealed class ImmediateScrollbackValue(
    IReadOnlyList<string> lines,
    bool muted,
    ScrollbackLayout layout) : IScrollbackItem
{
    public bool IsCompleted => true;

    public ScrollbackLayout Layout { get; } = layout;

    public static IScrollbackItem Muted(IReadOnlyList<string> lines) =>
        new ImmediateScrollbackValue(lines, true, ScrollbackLayout.Compact);

    public static IScrollbackItem Trusted(IReadOnlyList<string> lines) =>
        new ImmediateScrollbackValue(lines, false, ScrollbackLayout.Compact);

    public static IScrollbackItem User(string text) =>
        new ImmediateScrollbackValue([TrimLegacyUserPrefix(text)], false, ScrollbackLayout.User);

    public static IScrollbackItem Assistant(string text) =>
        new ImmediateScrollbackValue([text], false, ScrollbackLayout.Assistant);

    public bool Continues(IScrollbackItem previous) => false;

    public IReadOnlyList<string> Render(ScrollbackRenderContext context) => Layout switch
    {
        ScrollbackLayout.User => RenderMessage(
            TerminalIcons.UserMessage,
            lines[0],
            context.Columns,
            context.Palette.UserMessage),
        ScrollbackLayout.Assistant => RenderMessage(
            TerminalIcons.AssistantMessage,
            lines[0],
            context.Columns,
            context.Palette.AssistantMessage),
        _ when muted => [.. lines.Select(value => context.Palette.Muted.Apply(TerminalText.Sanitize(value)))],
        _ => lines,
    };

    private static IReadOnlyList<string> RenderMessage(
        string icon,
        string text,
        int columns,
        TerminalStyle style)
    {
        var prefix = icon + " ";
        var clean = prefix + TerminalText.Sanitize(text).TrimEnd('\r', '\n');
        var indent = new string(' ', TerminalText.Width(prefix));
        return [.. TerminalText.LayoutHanging(clean, columns, indent).Select(style.Apply)];
    }

    private static string TrimLegacyUserPrefix(string text)
    {
        var clean = TerminalText.Sanitize(text);
        return clean.StartsWith("› ", StringComparison.Ordinal) ? clean[2..] : clean;
    }
}
