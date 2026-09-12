namespace Parrot.Llm;

internal static class ChatGptModelCatalogue
{
    private static readonly HashSet<string> ExplicitlyAllowed = new(StringComparer.Ordinal)
    {
        "gpt-5.5",
        "gpt-5.3-codex-spark",
        "gpt-5.4",
        "gpt-5.4-mini",
    };

    private static readonly HashSet<string> ExplicitlyDisallowed = new(StringComparer.Ordinal)
    {
        "gpt-5.5-pro",
        "gpt-5.6",
    };

    public static IReadOnlyList<LLMModel> FilterExternal(IReadOnlyList<LLMModel> models) =>
        [.. models.Where(model => Includes(model.Id)).Select(model => model with { ProviderId = ChatGptProvider.ProviderId })];

    private static bool Includes(string modelId)
    {
        if (ExplicitlyAllowed.Contains(modelId))
        {
            return true;
        }

        if (ExplicitlyDisallowed.Contains(modelId) || !modelId.StartsWith("gpt-", StringComparison.Ordinal))
        {
            return false;
        }

        var version = modelId.AsSpan(4);
        var majorLength = ReadDigits(version);
        if (majorLength == 0)
        {
            return false;
        }

        var major = version[..majorLength];
        if (IsGreaterThan(major, 5))
        {
            return true;
        }

        if (!IsEqualTo(major, 5))
        {
            return false;
        }

        var minor = ReadMinor(version[majorLength..]);
        return minor.Length > 0 && IsGreaterThan(minor, 4);
    }

    private static ReadOnlySpan<char> ReadMinor(ReadOnlySpan<char> suffix)
    {
        if (suffix.Length < 2 || suffix[0] != '.' || !char.IsAsciiDigit(suffix[1]))
        {
            return [];
        }

        suffix = suffix[1..];
        return suffix[..ReadDigits(suffix)];
    }

    private static int ReadDigits(ReadOnlySpan<char> value)
    {
        var length = 0;
        while (length < value.Length && char.IsAsciiDigit(value[length]))
        {
            length++;
        }

        return length;
    }

    private static bool IsEqualTo(ReadOnlySpan<char> digits, int value)
    {
        digits = TrimLeadingZeroes(digits);
        return digits.Length == 1 && digits[0] - '0' == value;
    }

    private static bool IsGreaterThan(ReadOnlySpan<char> digits, int value)
    {
        digits = TrimLeadingZeroes(digits);
        return digits.Length > 1 || digits[0] - '0' > value;
    }

    private static ReadOnlySpan<char> TrimLeadingZeroes(ReadOnlySpan<char> digits)
    {
        var index = 0;
        while (index < digits.Length - 1 && digits[index] == '0')
        {
            index++;
        }

        return digits[index..];
    }
}
