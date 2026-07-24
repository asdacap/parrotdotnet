namespace Parrot.Agent;

internal sealed record AgentIdentity(string SessionId, string ParentSessionId, string Name, int Depth)
{
    public string Context =>
        $"Child agent session: {SessionId}\n"
        + $"Parent agent session: {ParentSessionId}\n"
        + $"Child agent name: {Name}\n"
        + $"Child agent depth: {Depth}";
}
