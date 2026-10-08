using Parrot.Cli.Enhanced.Tools;

namespace Parrot.Cli;

internal static class AgentDurationFormatter
{
    public static string Format(long elapsedMilliseconds) =>
        DurationText.Format(Math.Max(0L, elapsedMilliseconds + 500) / 1000);
}
