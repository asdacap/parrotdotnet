using Parrot.Agent;
using Parrot.Statuses;

namespace Parrot.Core.Tests;

internal sealed class StatisticsStatusProviderTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Reports_self_subtree_costs_efforts_and_legacy_precision(
        bool populated,
        CancellationToken cancellationToken)
    {
        var statistics = new AgentSessionStatistics(AgentSessionStatisticsSnapshot.Empty);
        if (populated)
        {
            statistics.AddOwn(new AgentUsageIncrement(
                new AgentUsageKey("provider", "model", "high"),
                new AgentUsageTotals(10, 4, 2, 1, 0.25, 0.5),
                null,
                null));
            statistics.AddDescendant(new AgentUsageIncrement(
                new AgentUsageKey("provider", "model", "high"),
                new AgentUsageTotals(10, 4, 2, 1, 0.25, 0.5),
                null,
                null));
            statistics.AddDescendant(new AgentUsageIncrement(
                new AgentUsageKey("provider", "model", null),
                AgentUsageTotals.Empty,
                null,
                null));
            statistics.AddDescendant(new AgentUsageIncrement(
                AgentUsageKey.Legacy, AgentUsageTotals.Empty, null, null));
        }

        IStatusProvider provider = new StatisticsStatusProvider(
            statistics.Capture() with { HasIncompleteLegacyToolCounts = populated },
            TestModels.PromptTemplates);
        var observation = await provider.Observe(
            new StatusQuery("agent", string.Empty, string.Empty, "build", "alias"), cancellationToken);

        _ = await Assert.That(observation.Available).IsTrue();
        _ = await Assert.That(observation.Text).Contains("Statistics (lifetime):");
        _ = await Assert.That(observation.Text).DoesNotContain("alias");
        if (populated)
        {
            _ = await Assert.That(observation.Text).Contains("Self: 10 (40.00% cache) / 2; 1 tool; cost $0.75 (input $0.25, output $0.50)");
            _ = await Assert.That(observation.Text).Contains("Cumulative (self + descendants): 20 (40.00% cache) / 4; 2 tool; cost $1.50 (input $0.50, output $1.00)");
            _ = await Assert.That(observation.Text).Contains("provider/model/high: 20 (40.00% cache)");
            _ = await Assert.That(observation.Text).Contains("provider/model/unspecified/default:");
            _ = await Assert.That(observation.Text).Contains("unknown/legacy/unknown (legacy):");
            _ = await Assert.That(observation.Text).Contains("Legacy tool-execution counts may be incomplete");
        }
        else
        {
            _ = await Assert.That(observation.Text).Contains("Self: 0 (0.00% cache) / 0; 0 tool; cost $0.00 (input $0.00, output $0.00)");
            _ = await Assert.That(observation.Text).Contains("Cumulative (self + descendants): 0 (0.00% cache)");
            _ = await Assert.That(observation.Text).DoesNotContain("may be incomplete");
        }
    }

    [Test]
    [Arguments(999L, "999")]
    [Arguments(1_000L, "1k")]
    [Arguments(99_949L, "99.9k")]
    [Arguments(1_000_000L, "1M")]
    [Arguments(2_629_445L, "2.6M")]
    public async Task Formats_token_counts_and_rounds_costs(long tokens, string formattedTokens, CancellationToken cancellationToken)
    {
        var statistics = new AgentSessionStatistics(AgentSessionStatisticsSnapshot.Empty);
        statistics.AddOwn(new AgentUsageIncrement(
            new AgentUsageKey("provider", "model", "high"),
            new AgentUsageTotals(tokens, tokens, tokens, 1, 3.8004980000000006, 0.4256999999999998),
            null,
            null));
        IStatusProvider provider = new StatisticsStatusProvider(statistics.Capture(), TestModels.PromptTemplates);
        var observation = await provider.Observe(
            new StatusQuery("agent", string.Empty, string.Empty, "build", "alias"), cancellationToken);

        var expectedTotals = $"{formattedTokens} (100.00% cache) / {formattedTokens}; " +
            "1 tool; cost $4.23 (input $3.80, output $0.43)";
        _ = await Assert.That(observation.Text).Contains($"Self: {expectedTotals}");
        _ = await Assert.That(observation.Text).Contains($"Cumulative (self + descendants): {expectedTotals}");
        _ = await Assert.That(observation.Text).Contains($"provider/model/high: {expectedTotals}");
    }
}
