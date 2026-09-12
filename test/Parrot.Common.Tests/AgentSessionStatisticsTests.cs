using Parrot.Agent;
using Parrot.Llm;

namespace Parrot.Core.Tests;

internal sealed class AgentSessionStatisticsTests
{
    [Test]
    [Arguments(1)]
    [Arguments(100)]
    public async Task Statistics_preserve_self_subtree_buckets_context_and_replay(int repetitions)
    {
        var model = new AgentUsageKey("provider", "model", "high");
        var otherEffort = model with { Effort = "low" };
        var unspecifiedEffort = model with { Effort = null };
        var root = new AgentSessionStatistics(AgentSessionStatisticsSnapshot.Empty);
        var child = new AgentSessionStatistics(AgentSessionStatisticsSnapshot.Empty);
        var grandchild = new AgentSessionStatistics(AgentSessionStatisticsSnapshot.Empty);
        var own = new AgentUsageIncrement(model, new(100, 25, 10, 0, 0.5, 0.25), 100, 1000);
        root.AddOwn(own);
        var originalSnapshot = root.Capture();
        var descendant = new AgentUsageIncrement(otherEffort, new(20, 5, 2, 0, 0.125, 0.0625), 20, 200);
        for (var index = 0; index < repetitions; index++)
        {
            grandchild.AddOwn(descendant);
            child.AddDescendant(descendant);
            root.AddDescendant(descendant);
            var tool = AgentUsageIncrement.FromToolStart(otherEffort);
            grandchild.AddOwn(tool);
            child.AddDescendant(tool);
            root.AddDescendant(tool);
        }

        root.AddDescendant(new(unspecifiedEffort, new(1, 0, 1, 1, 0, 0), 1, 10));
        root.AddDescendant(new(AgentUsageKey.Legacy, new(3, 1, 1, 0, 0, 0), null, null));
        var snapshot = root.Capture();
        _ = await Assert.That(snapshot.Self.Totals).IsEqualTo(own.Totals);
        _ = await Assert.That(snapshot.Cumulative.Totals.InputTokens).IsEqualTo(104 + (20L * repetitions));
        _ = await Assert.That(snapshot.Cumulative.Totals.CachedInputTokens).IsEqualTo(26 + (5L * repetitions));
        _ = await Assert.That(snapshot.Cumulative.Totals.OutputTokens).IsEqualTo(12 + (2L * repetitions));
        _ = await Assert.That(snapshot.Cumulative.Totals.ToolCalls).IsEqualTo(repetitions + 1L);
        _ = await Assert.That(snapshot.Cumulative.Totals.TotalCost).IsEqualTo(0.75 + (0.1875 * repetitions));
        _ = await Assert.That(snapshot.Cumulative.Models.Count).IsEqualTo(4);
        _ = await Assert.That(snapshot.ContextSize).IsEqualTo(100);
        _ = await Assert.That(snapshot.ContextLimit).IsEqualTo(1000);
        _ = await Assert.That(snapshot.ToSelfStatistics().InputCost).IsEqualTo(0.5);
        _ = await Assert.That(child.Capture().Self.Totals).IsEqualTo(AgentUsageTotals.Empty);
        _ = await Assert.That(child.Capture().Cumulative.Totals).IsEqualTo(grandchild.Capture().Self.Totals);
        _ = await Assert.That(originalSnapshot.Cumulative.Totals).IsEqualTo(own.Totals);
        var restored = new AgentSessionStatistics(snapshot);
        restored.AddOwn(AgentUsageIncrement.FromToolStart(model));
        _ = await Assert.That(restored.Capture().Self.Totals.ToolCalls).IsEqualTo(1);
        _ = await Assert.That(restored.Capture().Cumulative.Totals.ToolCalls).IsEqualTo(repetitions + 2L);
        _ = await Assert.That(snapshot.Self.Totals.ToolCalls).IsEqualTo(0);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Request_usage_uses_canonical_identity_actual_effort_and_cached_pricing(bool cachedPrice)
    {
        ILLMProvider provider = new TerminalFailureProvider("unused");
        var model = new LLMModel("canonical-model", provider.Id)
        {
            InputPrice = 0.125,
            CachedInputPrice = 0.0625,
            OutputPrice = 0.25,
            ContextWindow = 1000,
            Fields = cachedPrice ? ModelMetadataFields.CachedInputPrice : ModelMetadataFields.None,
        };
        var selected = new ProviderModel(provider, model, new("preset-name", "low"));
        var reasoning = new ReasoningOptions("high", "auto");
        var completed = LLMEvent.Completed(string.Empty, 100, 20, 10, string.Empty, []);
        var increment = AgentUsageIncrement.FromCompletion(selected, reasoning, completed);
        _ = await Assert.That(increment.Key).IsEqualTo(new AgentUsageKey(provider.Id, model.Id, "high"));
        _ = await Assert.That(increment.Key).IsEqualTo(
            AgentUsageKey.FromRequest(new(provider, model, new("other-preset", "low")), reasoning));
        _ = await Assert.That(increment.Totals.InputTokens).IsEqualTo(100);
        _ = await Assert.That(increment.Totals.CachedInputTokens).IsEqualTo(20);
        _ = await Assert.That(increment.Totals.OutputTokens).IsEqualTo(10);
        _ = await Assert.That(increment.Totals.InputCost).IsEqualTo(cachedPrice ? 11.25 : 12.5);
        _ = await Assert.That(increment.Totals.OutputCost).IsEqualTo(2.5);
        _ = await Assert.That(increment.ContextLimit).IsEqualTo(1000);
    }

    [Test]
    public async Task Concurrent_sibling_and_own_updates_do_not_lose_usage(CancellationToken cancellationToken)
    {
        var statistics = new AgentSessionStatistics(AgentSessionStatisticsSnapshot.Empty);
        var increment = AgentUsageIncrement.FromToolStart(new("provider", "model", null));
        await Parallel.ForAsync(0, 1000, cancellationToken, (index, _) =>
        {
            if (index % 2 == 0)
            {
                statistics.AddOwn(increment);
            }
            else
            {
                statistics.AddDescendant(increment);
            }

            return ValueTask.CompletedTask;
        });
        var snapshot = statistics.Capture();
        _ = await Assert.That(snapshot.Self.Totals.ToolCalls).IsEqualTo(500);
        _ = await Assert.That(snapshot.Cumulative.Totals.ToolCalls).IsEqualTo(1000);
        _ = await Assert.That(snapshot.Cumulative.Models[increment.Key].ToolCalls).IsEqualTo(1000);
    }
}
