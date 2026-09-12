using Parrot.Events;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class UserSessionStatistics(AgentStatisticsReplay replay) : IUserSessionStatistics
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, AgentSessionStatistics> _agents = replay.Agents.ToDictionary(
        static pair => pair.Key,
        pair => new AgentSessionStatistics(pair.Value with
        {
            HasIncompleteLegacyToolCounts = ContainsIncompleteLegacyTools(replay, pair.Key),
        }),
        StringComparer.Ordinal);

    private long _revision = replay.Revision;

    public AgentSessionStatistics GetAgentStatistics(string sessionId)
    {
        lock (_gate)
        {
            if (!_agents.TryGetValue(sessionId, out var statistics))
            {
                statistics = new(AgentSessionStatisticsSnapshot.Empty);
                _agents.Add(sessionId, statistics);
            }

            return statistics;
        }
    }

    public SessionUsageSnapshot CaptureUsage(string rootSessionId)
    {
        lock (_gate)
        {
            var statistics = GetAgentStatistics(rootSessionId).Capture();
            var totals = statistics.Cumulative.Totals;
            return new()
            {
                Revision = checked((ulong)_revision),
                InputTokens = totals.InputTokens,
                CachedInputTokens = totals.CachedInputTokens,
                OutputTokens = totals.OutputTokens,
                InputCost = totals.InputCost,
                OutputCost = totals.OutputCost,
                ContextSize = statistics.ContextSize,
                ContextLimit = statistics.ContextLimit,
            };
        }
    }

    public void RecordUsage(
        IEventRepository repository,
        IEventBroker broker,
        Event fact,
        AgentUsageIncrement increment,
        Action<AgentUsageIncrement> apply,
        string rootSessionId)
    {
        lock (_gate)
        {
            var revision = repository.AppendUsageFact(fact);
            if (revision <= _revision)
            {
                return;
            }

            apply(increment);
            _revision = revision;
            broker.Publish(fact);
            broker.Publish(new Event
            {
                AgentSessionId = fact.AgentSessionId,
                AgentStatisticsUpdated = AgentStatisticsUpdatedEvent.From(GetAgentStatistics(fact.AgentSessionId).Capture().ToSelfStatistics()),
            });
            broker.Publish(new Event { SessionUsageSnapshot = CaptureUsage(rootSessionId) });
        }
    }

    private static bool ContainsIncompleteLegacyTools(AgentStatisticsReplay replay, string sessionId)
    {
        foreach (var incomplete in replay.IncompleteLegacyToolCounts)
        {
            var ancestor = incomplete;
            while (ancestor.Length > 0)
            {
                if (string.Equals(ancestor, sessionId, StringComparison.Ordinal))
                {
                    return true;
                }

                ancestor = replay.Parents.GetValueOrDefault(ancestor, string.Empty);
            }
        }

        return false;
    }
}
