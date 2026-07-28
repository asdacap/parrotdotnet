namespace Parrot.Agent;

internal sealed record AgentIdentity(
    string SessionId,
    string ParentSessionId,
    string ParentSessionName,
    string Name,
    int Depth)
{
    public string Context =>
        Depth == 0
            ? string.Empty
            : $"Child agent session: {SessionId}\n"
            + $"Parent agent session: {ParentSessionId}\n"
            + $"Parent agent name: {ParentSessionName}\n"
            + $"Child agent name: {Name}\n"
            + $"Child agent depth: {Depth}";

    public static AgentIdentity Main(string sessionId, string rootAgentName) =>
        new(sessionId, string.Empty, string.Empty, rootAgentName, 0);

    public static AgentIdentity Child(
        string sessionId,
        string parentSessionId,
        string parentSessionName,
        string name,
        int depth) =>
        new(sessionId, parentSessionId, parentSessionName, name, depth);
}
