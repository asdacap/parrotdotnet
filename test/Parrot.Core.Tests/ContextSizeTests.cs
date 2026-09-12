using Parrot.Context;

namespace Parrot.Core.Tests;

internal sealed class ContextSizeTests
{
    [Test]
    [Arguments("100k", 100_000L, false)]
    [Arguments(" 20% ", 20L, true)]
    [Arguments("1", 1L, false)]
    [Arguments("100%", 100L, true)]
    [Arguments("2K", 2_000L, false)]
    [Arguments("9223372036854775807", long.MaxValue, false)]
    public async Task Parses_and_round_trips_sizes(string text, long value, bool percentage)
    {
        var size = ContextSize.Parse(text);
        _ = await Assert.That(size.Value).IsEqualTo(value);
        _ = await Assert.That(size.IsPercentage).IsEqualTo(percentage);
        _ = await Assert.That(ContextSize.Parse(size.ToString())).IsEqualTo(size);
        _ = await Assert.That(size.ResolveTokens(1_000)).IsEqualTo(percentage ? value * 10 : value);
    }

    [Test]
    [Arguments("")]
    [Arguments("0")]
    [Arguments("-1")]
    [Arguments("+1")]
    [Arguments("0%")]
    [Arguments("101%")]
    [Arguments("1.5k")]
    [Arguments("1m")]
    [Arguments("1,000")]
    [Arguments("0k")]
    [Arguments("20 %")]
    [Arguments("20% trailing")]
    [Arguments("9223372036854775808")]
    [Arguments("9223372036854776k")]
    public async Task Rejects_invalid_sizes(string text) =>
        _ = await Assert.That(() => ContextSize.Parse(text)).Throws<FormatException>();

    [Test]
    [Arguments(null, null, 1_000, 800, 720L, 240L)]
    [Arguments("20%", null, 1_000, 800, 200L, 60L)]
    [Arguments("100k", null, 1_000, 800, 800L, 240L)]
    [Arguments("100", "20%", 1_000, 800, 100L, 200L)]
    [Arguments("100", "100k", 1_000, 800, 100L, 800L)]
    [Arguments("100", null, 0, 800, 100L, 30L)]
    [Arguments("20%", null, 0, 800, null, null)]
    [Arguments(null, "20%", 0, 800, 720L, null)]
    [Arguments(null, null, 0, 0, null, null)]
    [Arguments("9223372036854775807", null, 0, 0, long.MaxValue, 2_767_011_611_056_432_742L)]
    public async Task Resolves_window_percentages_caps_and_legacy_defaults(
        string? limit,
        string? target,
        int window,
        int input,
        long? expectedTrigger,
        long? expectedTarget)
    {
        var policy = ContextCompactionPolicy.Resolve(
            limit is null ? null : ContextSize.Parse(limit),
            target is null ? null : ContextSize.Parse(target),
            window,
            input,
            90,
            30);
        _ = await Assert.That(policy.TriggerTokens).IsEqualTo(expectedTrigger);
        _ = await Assert.That(policy.TargetTokens).IsEqualTo(expectedTarget);
    }
}
