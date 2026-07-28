using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class RuntimeUsageTracker
{
    private readonly Dictionary<string, string> _parents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentStatisticsUpdatedEvent> _statistics = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentStatisticsUpdatedEvent> _turnBaselines = new(StringComparer.Ordinal);
    private string _rootSessionId = string.Empty;

    public RuntimeUsage Current
    {
        get
        {
            var sessions = ReachableSessions();
            long input = 0;
            long cached = 0;
            long output = 0;
            double cost = 0;
            long contextSize = 0;
            long contextLimit = 0;
            foreach (var session in sessions)
            {
                if (!_statistics.TryGetValue(session, out var statistics))
                {
                    continue;
                }

                _ = _turnBaselines.TryGetValue(session, out var baseline);
                input = checked(input + Math.Max(0, statistics.InputTokens - (baseline?.InputTokens ?? 0)));
                cached = checked(cached + Math.Max(0, statistics.CachedInputTokens - (baseline?.CachedInputTokens ?? 0)));
                output = checked(output + Math.Max(0, statistics.OutputTokens - (baseline?.OutputTokens ?? 0)));
                cost += Math.Max(0, statistics.InputCost - (baseline?.InputCost ?? 0))
                        + Math.Max(0, statistics.OutputCost - (baseline?.OutputCost ?? 0));
                if (string.Equals(session, _rootSessionId, StringComparison.Ordinal))
                {
                    contextSize = statistics.ContextSize;
                    contextLimit = statistics.ContextLimit;
                }
            }

            return new RuntimeUsage(input, cached, output, contextSize, contextLimit, cost);
        }
    }

    public void Observe(Event published)
    {
        ArgumentNullException.ThrowIfNull(published);

        if (published.PayloadCase == Event.PayloadOneofCase.AgentStarted)
        {
            _parents[published.AgentSessionId] = published.AgentStarted.ParentAgentSessionId;
        }
        else if (published.PayloadCase == Event.PayloadOneofCase.TurnStarted
                 && !_parents.ContainsKey(published.AgentSessionId))
        {
            _rootSessionId = published.AgentSessionId;
            _turnBaselines.Clear();
            foreach (var (session, statistics) in _statistics)
            {
                _turnBaselines.Add(session, statistics.Clone());
            }
        }
        else if (published.PayloadCase == Event.PayloadOneofCase.AgentStatisticsUpdated)
        {
            _statistics[published.AgentSessionId] = published.AgentStatisticsUpdated.Clone();
        }
    }

    public void Reset()
    {
        _rootSessionId = string.Empty;
        _parents.Clear();
        _statistics.Clear();
        _turnBaselines.Clear();
    }

    private HashSet<string> ReachableSessions()
    {
        if (_rootSessionId.Length == 0)
        {
            return [];
        }

        var reachable = new HashSet<string>(StringComparer.Ordinal) { _rootSessionId };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (session, parent) in _parents)
            {
                if (reachable.Contains(parent) && reachable.Add(session))
                {
                    changed = true;
                }
            }
        }

        return reachable;
    }
}
