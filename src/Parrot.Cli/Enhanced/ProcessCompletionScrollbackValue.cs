namespace Parrot.Cli.Enhanced;

internal sealed class ProcessCompletionScrollbackValue(string command, long? elapsedMilliseconds) : IScrollbackItem
{
    private const long DurationThresholdMilliseconds = 5_000;

    public bool IsCompleted => true;

    public ScrollbackLayout Layout => ScrollbackLayout.Compact;

    public long? ElapsedMilliseconds { get; } = elapsedMilliseconds;

    public bool Continues(IScrollbackItem previous) => false;

    public IReadOnlyList<string> Render(ScrollbackRenderContext context)
    {
        var label = ElapsedMilliseconds is > DurationThresholdMilliseconds
            ? $"{command} ({AgentDurationFormatter.Format(ElapsedMilliseconds.Value)})"
            : command;
        return [.. TerminalText.Layout(TerminalText.Sanitize(label), context.Columns)
            .Select(context.Palette.Muted.Apply)];
    }
}
