using System.Globalization;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class RunningDuration
{
    private readonly long _started;
    private readonly TimeProvider _timeProvider;

    public RunningDuration(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _started = timeProvider.GetTimestamp();
    }

    public string Format()
    {
        var totalSeconds = Math.Max(0L, (long)_timeProvider.GetElapsedTime(_started).TotalSeconds);
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
