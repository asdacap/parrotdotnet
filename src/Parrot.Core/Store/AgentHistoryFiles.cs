namespace Parrot.Store;

internal sealed class AgentHistoryFiles(UserSessionResources resources)
{
    private readonly Dictionary<string, Lock> _gates = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public AgentHistoryFile PathFor(string agentSessionId) =>
        new(resources.AgentHistoryFile(agentSessionId), Gate(agentSessionId));

    public void Publish(string agentSessionId, IReadOnlyList<AgentHistoryEntry> entries) =>
        PathFor(agentSessionId).Replace(entries);

    private Lock Gate(string agentSessionId)
    {
        _ = resources.AgentHistoryFile(agentSessionId);
        lock (_gate)
        {
            if (!_gates.TryGetValue(agentSessionId, out var gate))
            {
                gate = new Lock();
                _gates.Add(agentSessionId, gate);
            }

            return gate;
        }
    }
}
