using System.Globalization;

namespace Parrot.Cli.Enhanced;

internal readonly record struct RuntimeUsage(
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ContextSize,
    long ContextLimit,
    double Cost)
{
    public string FormatTokens()
    {
        if (InputTokens == 0 && OutputTokens == 0)
        {
            return string.Empty;
        }

        var value = $"+{FormatTokenCount(InputTokens)}i +{FormatTokenCount(OutputTokens)}o";
        return CachedInputTokens > 0 && InputTokens > 0
            ? value + $" (+{((double)CachedInputTokens / InputTokens * 100).ToString("0.00", CultureInfo.InvariantCulture)}% cache)"
            : value;
    }

    public string FormatContext() => ContextLimit > 0
        ? $"{FormatTokenCount(ContextSize)}/{FormatTokenCount(ContextLimit)}"
        : ContextSize > 0
            ? FormatTokenCount(ContextSize)
            : string.Empty;

    public static string FormatRate(TokenRate rate) => rate.HasTokens
        ? $"{rate.InputTokensPerSecond.ToString("0.##", CultureInfo.InvariantCulture)}i/s "
          + $"{rate.OutputTokensPerSecond.ToString("0.##", CultureInfo.InvariantCulture)}o/s"
        : string.Empty;

    public string FormatCost() => Cost switch
    {
        <= 0 => string.Empty,
        < 0.01 => Cost.ToString("$0.0000", CultureInfo.InvariantCulture),
        _ => Cost.ToString("$0.00", CultureInfo.InvariantCulture),
    };

    private static string FormatTokenCount(long count) => Math.Abs(count) switch
    {
        >= 1_000_000 => (count / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (count / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => count.ToString(CultureInfo.InvariantCulture),
    };
}
