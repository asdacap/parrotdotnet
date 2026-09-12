namespace Parrot.Agent;

internal sealed record ActiveAgentSnapshot(
    string SessionId,
    string ParentSessionId,
    string Name);
