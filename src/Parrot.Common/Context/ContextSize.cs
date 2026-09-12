using System.Globalization;

namespace Parrot.Context;

internal sealed record ContextSize
{
    private ContextSize(long value, bool percentage)
    {
        Value = value;
        IsPercentage = percentage;
    }

    public long Value { get; }

    public bool IsPercentage { get; }

    public static ContextSize Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var value = text.AsSpan().Trim();
        var percentage = value.EndsWith("%", StringComparison.Ordinal);
        var thousands = value.EndsWith("k", StringComparison.OrdinalIgnoreCase);
        var digits = percentage || thousands ? value[..^1] : value;
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0
            || (percentage && parsed > 100)
            || (thousands && parsed > long.MaxValue / 1000))
        {
            throw new FormatException("Context size must be positive whole tokens, an integer k suffix, or a whole percentage from 1% to 100%.");
        }

        return new ContextSize(thousands ? parsed * 1000 : parsed, percentage);
    }

    public long? ResolveTokens(int contextWindow) => IsPercentage
        ? contextWindow > 0 ? (long)contextWindow * Value / 100 : null
        : Value;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture) + (IsPercentage ? "%" : string.Empty);
}
