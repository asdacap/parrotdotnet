using System.Collections.Immutable;

namespace Parrot.Agent;

internal sealed record AgentUsageSnapshot(
    AgentUsageTotals Totals,
    ImmutableDictionary<AgentUsageKey, AgentUsageTotals> Models)
{
    public static AgentUsageSnapshot Empty { get; } = new(
        AgentUsageTotals.Empty,
        []);

    public AgentUsageSnapshot Add(AgentUsageIncrement increment) => new(
        Totals.Add(increment.Totals),
        Models.SetItem(
            increment.Key,
            Models.GetValueOrDefault(increment.Key, AgentUsageTotals.Empty).Add(increment.Totals)));
}
