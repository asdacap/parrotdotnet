namespace Parrot.Agent;

internal sealed record AgentIdentity(
    string SessionId,
    string ParentSessionId,
    string ParentSessionName,
    string Name,
    int Depth,
    AgentScope Scope)
{
    public string Context
    {
        get
        {
            var scope = Scope.Format(Depth);
            if (Depth == 0)
            {
                return scope;
            }

            var identity = $"Child agent session: {SessionId}\n"
                + $"Parent agent session: {ParentSessionId}\n"
                + $"Parent agent name: {ParentSessionName}\n"
                + $"Child agent name: {Name}\n"
                + $"Child agent depth: {Depth}";
            return scope.Length == 0
                ? identity
                : $"{identity}\n\n{scope}";
        }
    }

    public static AgentIdentity Main(string sessionId, string rootAgentName) =>
        new(sessionId, string.Empty, string.Empty, rootAgentName, 0, AgentScope.Empty);

    public static AgentIdentity Child(
        string sessionId,
        string parentSessionId,
        string parentSessionName,
        string name,
        int depth,
        AgentScope scope) =>
        new(sessionId, parentSessionId, parentSessionName, name, depth, scope);
}
