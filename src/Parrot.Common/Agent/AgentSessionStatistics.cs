namespace Parrot.Agent;

internal sealed class AgentSessionStatistics(AgentSessionStatisticsSnapshot initial)
{
    private readonly Lock _gate = new();
    private AgentSessionStatisticsSnapshot _snapshot = initial;

    public AgentSessionStatisticsSnapshot Capture()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    public void AddOwn(AgentUsageIncrement increment)
    {
        lock (_gate)
        {
            _snapshot = new(
                _snapshot.Self.Add(increment),
                _snapshot.Cumulative.Add(increment),
                increment.ContextSize ?? _snapshot.ContextSize,
                increment.ContextLimit ?? _snapshot.ContextLimit)
            {
                HasIncompleteLegacyToolCounts = _snapshot.HasIncompleteLegacyToolCounts,
            };
        }
    }

    public void AddDescendant(AgentUsageIncrement increment)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with { Cumulative = _snapshot.Cumulative.Add(increment) };
        }
    }
}
