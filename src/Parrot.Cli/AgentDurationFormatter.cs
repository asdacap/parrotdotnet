using System.Globalization;

namespace Parrot.Cli;

internal static class AgentDurationFormatter
{
    public static string Format(long elapsedMilliseconds)
    {
        var totalSeconds = Math.Max(0L, elapsedMilliseconds + 500) / 1000;
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
