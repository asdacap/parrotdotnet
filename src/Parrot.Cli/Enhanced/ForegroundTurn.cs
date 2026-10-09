using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class ForegroundTurn
{
    private readonly AgentSessionHierarchy _hierarchy = new();

    public bool IsReplaying { get; private set; }

    public void ObserveReplay(bool replaying) => IsReplaying = replaying;

    public void Observe(Event published) => _hierarchy.Observe(published);

    public void Reset() => _hierarchy.Reset();

    public bool IsMain(string agentSessionId) => _hierarchy.IsRoot(agentSessionId);

    public bool IsChild(string agentSessionId) => _hierarchy.IsChild(agentSessionId);

    public bool IsTerminal(Event published) =>
        !IsReplaying
        && published.PayloadCase is Event.PayloadOneofCase.ModeTurnCompleted or Event.PayloadOneofCase.TurnFailed
        && IsMain(published.AgentSessionId);
}
