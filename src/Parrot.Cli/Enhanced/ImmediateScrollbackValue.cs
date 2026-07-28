namespace Parrot.Cli.Enhanced;

internal sealed class ImmediateScrollbackValue : IScrollbackItem
{
    private readonly IReadOnlyList<string> _lines;
    private readonly bool _muted;
    private readonly bool _user;

    private ImmediateScrollbackValue(IReadOnlyList<string> lines, bool muted, bool user)
    {
        _lines = lines;
        _muted = muted;
        _user = user;
    }

    public bool IsCompleted => true;

    public static IScrollbackItem Muted(IReadOnlyList<string> lines) =>
        new ImmediateScrollbackValue(lines, true, false);

    public static IScrollbackItem Trusted(IReadOnlyList<string> lines) =>
        new ImmediateScrollbackValue(lines, false, false);

    public static IScrollbackItem User(string text) =>
        new ImmediateScrollbackValue([text], false, true);

    public bool Continues(IScrollbackItem previous) => false;

    public IReadOnlyList<string> Render(ScrollbackRenderContext context) => _user
        ? [.. TerminalText.Layout(TerminalText.Sanitize(_lines[0]).TrimEnd('\r', '\n'), context.Columns)
            .Select(context.Palette.User.Apply)]
        : _muted
            ? [.. _lines.Select(value => context.Palette.Muted.Apply(TerminalText.Sanitize(value)))]
            : _lines;
}
