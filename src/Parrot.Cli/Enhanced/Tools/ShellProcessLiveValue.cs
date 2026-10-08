using Parrot.Protocol;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class ShellProcessLiveValue(
    ActiveShellProcess process,
    long observedTimestamp,
    TimeProvider timeProvider,
    int frame) : ILiveBufferItem
{
    public MultiLine Render(LiveBufferRenderContext context)
    {
        var elapsedSinceSnapshot = timeProvider.GetElapsedTime(observedTimestamp);
        var elapsedMilliseconds = Math.Max(0L, process.ElapsedMs + (long)elapsedSinceSnapshot.TotalMilliseconds);
        var name = process.Name.Length == 0 ? process.ProcessId : process.Name;
        var summary = ExecCommandToolPresenter.Summarize(name, process.Description, process.Command);
        var label = $"$ {summary} (process {name} running {DurationText.Format(elapsedMilliseconds / 1000)})";
        var marker = TerminalIcons.SpinnerFrames[frame % TerminalIcons.SpinnerFrames.Length].ToString();
        var lines = context.Decoration.Apply(
                marker,
                TerminalText.LayoutWords(TerminalText.Sanitize(label), context.Decoration.ContentColumns(context.Columns)).Take(10))
            .Select(value => new TerminalLine(value, context.Palette.Marker))
            .ToList();
        return new MultiLine(lines, null, LiveBufferRetention.Fixed);
    }
}
