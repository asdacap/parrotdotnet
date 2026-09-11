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
        _ = await Assert.That(observation.Text).Contains("Statistics for agent agent (lifetime):");
        _ = await Assert.That(observation.Text).DoesNotContain("alias");
        if (populated)
        {
            _ = await Assert.That(observation.Text).Contains("Self: 10 input / 4 cached / 2 output tokens; 1 started tool executions; cost $0.75 (input $0.25, output $0.5)");
            _ = await Assert.That(observation.Text).Contains("Cumulative (self + descendants): 20 input / 8 cached / 4 output tokens; 2 started tool executions; cost $1.5 (input $0.5, output $1)");
            _ = await Assert.That(observation.Text).Contains("provider/model; effort high: 20 input");
            _ = await Assert.That(observation.Text).Contains("provider/model; effort unspecified/default:");
            _ = await Assert.That(observation.Text).Contains("unknown/legacy; effort unknown (legacy):");
            _ = await Assert.That(observation.Text).Contains("Legacy tool-execution counts may be incomplete");
        }
        else
        {
            _ = await Assert.That(observation.Text).Contains("Self: 0 input / 0 cached / 0 output tokens; 0 started tool executions; cost $0 (input $0, output $0)");
            _ = await Assert.That(observation.Text).Contains("Cumulative (self + descendants): 0 input");
            _ = await Assert.That(observation.Text).DoesNotContain("may be incomplete");
        }
    }
}
