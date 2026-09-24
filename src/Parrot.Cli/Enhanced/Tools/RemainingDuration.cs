namespace Parrot.Cli.Enhanced.Tools;

internal sealed class RemainingDuration(TimeProvider timeProvider, TimeSpan duration)
{
    private readonly long _started = timeProvider.GetTimestamp();

    public string Format() =>
        DurationText.Format(Math.Max(0L, (long)Math.Ceiling((duration - timeProvider.GetElapsedTime(_started)).TotalSeconds)));
}
