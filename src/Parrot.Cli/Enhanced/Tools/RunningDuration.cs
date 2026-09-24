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

    public string Format() =>
        DurationText.Format(Math.Max(0L, (long)_timeProvider.GetElapsedTime(_started).TotalSeconds));
}
