using System.Globalization;
using Parrot.Statuses;

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

        var value = $"+{TokenCountFormatter.Format(InputTokens)}i +{TokenCountFormatter.Format(OutputTokens)}o";
        return CachedInputTokens > 0 && InputTokens > 0
            ? value + $" (+{((double)CachedInputTokens / InputTokens * 100).ToString("0.00", CultureInfo.InvariantCulture)}% cache)"
            : value;
    }

    public string FormatContext() => ContextLimit > 0
        ? $"{TokenCountFormatter.Format(ContextSize)}/{TokenCountFormatter.Format(ContextLimit)}"
        : ContextSize > 0
            ? TokenCountFormatter.Format(ContextSize)
            : string.Empty;

    public static string FormatRate(TokenRate rate) => rate.HasTokens
        ? $"{FormatTokenRate(rate.InputTokensPerSecond)}i/s {FormatTokenRate(rate.OutputTokensPerSecond)}o/s"
        : string.Empty;

    public string FormatCost() => Cost switch
    {
        <= 0 => string.Empty,
        < 0.01 => Cost.ToString("$0.0000", CultureInfo.InvariantCulture),
        _ => Cost.ToString("$0.00", CultureInfo.InvariantCulture),
    };

    private static string FormatTokenRate(decimal rate) => Math.Abs(rate) switch
    {
        >= 1_000_000 => (rate / 1_000_000m).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (rate / 1_000m).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => rate.ToString("0.##", CultureInfo.InvariantCulture),
    };
}
