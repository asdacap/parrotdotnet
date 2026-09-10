namespace Parrot.Cli.Enhanced;

internal sealed class QuestionCountdown(TimeProvider timeProvider)
{
    private long? _remainingMilliseconds;
    private long _sampledAt;

    public void Update(long? remainingMilliseconds)
    {
        _remainingMilliseconds = remainingMilliseconds;
        _sampledAt = timeProvider.GetTimestamp();
    }

    public long? GetRemainingSeconds() => _remainingMilliseconds is { } remainingMilliseconds
        ? (long)Math.Ceiling(Math.Max(0, remainingMilliseconds - timeProvider.GetElapsedTime(_sampledAt).TotalMilliseconds) / 1000)
        : null;
}
