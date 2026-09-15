using System.Globalization;
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
        var label = $"$ {process.Command} (process {name} running {Format(elapsedMilliseconds)})";
        var marker = TerminalIcons.SpinnerFrames[frame % TerminalIcons.SpinnerFrames.Length].ToString();
        var lines = context.Decoration.Apply(
                marker,
                TerminalText.Layout(TerminalText.Sanitize(label), context.Decoration.ContentColumns(context.Columns)).Take(10))
            .Select(value => new TerminalLine(value, context.Palette.Marker))
            .ToList();
        return new MultiLine(lines, null, LiveBufferRetention.Fixed);
    }

    private static string Format(long elapsedMilliseconds)
    {
        var totalSeconds = elapsedMilliseconds / 1000;
        return totalSeconds switch
        {
            < 60 => $"{totalSeconds.ToString(CultureInfo.InvariantCulture)}s",
            < 3600 => string.Create(
                CultureInfo.InvariantCulture,
                $"{totalSeconds / 60}m {totalSeconds % 60:00}s"),
            _ => string.Create(
                CultureInfo.InvariantCulture,
                $"{totalSeconds / 3600}h {(totalSeconds / 60) % 60:00}m {totalSeconds % 60:00}s"),
        };
    }
}
