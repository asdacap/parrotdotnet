using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class ForegroundTurn
{
    private readonly HashSet<string> _children = new(StringComparer.Ordinal);
    private string? _agentSessionId;

    public void Observe(Event published)
    {
        ArgumentNullException.ThrowIfNull(published);

        if (published.PayloadCase == Event.PayloadOneofCase.AgentStarted)
        {
            _ = _children.Add(published.AgentSessionId);
        }
        else if (published.PayloadCase == Event.PayloadOneofCase.TurnStarted
                 && _agentSessionId is null
                 && !_children.Contains(published.AgentSessionId))
        {
            _agentSessionId = published.AgentSessionId;
        }
    }

    public bool IsMain(string agentSessionId) =>
        _agentSessionId is not null && string.Equals(_agentSessionId, agentSessionId, StringComparison.Ordinal);

    public bool IsTerminal(Event published) =>
        published.PayloadCase is Event.PayloadOneofCase.TurnEnded or Event.PayloadOneofCase.TurnFailed
        && IsMain(published.AgentSessionId);
}
