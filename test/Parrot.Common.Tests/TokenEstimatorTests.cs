using Parrot.Context;

namespace Parrot.Core.Tests;

internal sealed class TokenEstimatorTests
{
    public static IEnumerable<object[]> Texts()
    {
        yield return [string.Empty, 0L];
        yield return ["a", 1L];
        yield return ["abcd", 1L];
        yield return ["abcde", 2L];
        yield return [new string('a', 4_001), 1_001L];
        yield return ["日本語のテキスト", 2L];
        yield return ["🙂", 1L];
    }

    [Test]
    [MethodDataSource(nameof(Texts))]
    public async Task Estimates_tokens_as_quarters_rounded_up(string text, long expected) =>
        _ = await Assert.That(TokenEstimator.EstimateTokens(text)).IsEqualTo(expected);
}
