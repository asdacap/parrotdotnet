namespace Parrot.Context;

internal static class TokenEstimator
{
    public static long EstimateTokens(string text) => (text.Length + 3L) / 4L;
}
