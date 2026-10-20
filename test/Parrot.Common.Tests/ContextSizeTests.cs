using Parrot.Context;
using Parrot.Llm;

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
            new LLMModel("model", "provider")
            {
                ContextWindow = window,
                MaxInputTokens = input,
                MaxOutputTokens = 10,
            },
            90,
            30);
        _ = await Assert.That(policy.TriggerTokens).IsEqualTo(expectedTrigger);
        _ = await Assert.That(policy.TargetTokens).IsEqualTo(expectedTarget);
    }

    [Test]
    [Arguments(null, 54_000L)]
    [Arguments("180k", 54_000L)]
    [Arguments("100k", 30_000L)]
    public async Task Output_reservation_does_not_change_summary_targets(string? limit, long expectedTarget)
    {
        var model = new LLMModel("model", "provider") { ContextWindow = 180_000 };
        var policy = ContextCompactionPolicy.Resolve(
            limit is null ? null : ContextSize.Parse(limit), null, model, 95, 30);

        _ = await Assert.That(policy.TargetTokens).IsEqualTo(expectedTarget);
    }

    [Test]
    [Arguments(null, 180_000, 0, 0, 95, 140_220L)]
    [Arguments(null, 180_000, 0, 65_536, 95, 140_220L)]
    [Arguments(null, 180_000, 0, 8_192, 95, 163_626L)]
    [Arguments(null, 180_000, 0, 0, 50, 90_000L)]
    [Arguments("100k", 180_000, 0, 0, 95, 100_000L)]
    [Arguments("95%", 180_000, 0, 0, 95, 140_220L)]
    [Arguments("180k", 180_000, 0, 0, 95, 140_220L)]
    [Arguments(null, 180_000, 100_000, 0, 95, 95_000L)]
    [Arguments("180k", 180_000, 100_000, 0, 95, 100_000L)]
    [Arguments(null, 0, 100_000, 0, 95, 95_000L)]
    [Arguments("100k", 0, 100_000, 0, 95, 100_000L)]
    [Arguments(null, 1_000, 0, 0, 95, 0L)]
    [Arguments(null, int.MaxValue, 0, 0, 100, 2_045_191_313L)]
    public async Task Trigger_preserves_user_threshold_but_reserves_output_and_headroom(
        string? limit,
        int window,
        int input,
        int output,
        int triggerPercent,
        long expectedTrigger)
    {
        var model = new LLMModel("model", "provider")
        {
            ContextWindow = window,
            MaxInputTokens = input,
            MaxOutputTokens = output,
        };
        var policy = ContextCompactionPolicy.Resolve(
            limit is null ? null : ContextSize.Parse(limit), null, model, triggerPercent, 30);
        var snapshot = new ContextSnapshot(expectedTrigger, window, null, triggerPercent)
        {
            TriggerTokens = policy.TriggerTokens,
        };

        _ = await Assert.That(policy.TriggerTokens).IsEqualTo(expectedTrigger);
        _ = await Assert.That(snapshot.ExceedsTrigger).IsFalse();
        _ = await Assert.That(snapshot.ExceedsCompactionTrigger(expectedTrigger + 1)).IsTrue();
    }
}
