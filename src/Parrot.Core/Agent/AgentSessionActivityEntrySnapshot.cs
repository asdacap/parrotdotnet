namespace Parrot.Agent;

internal sealed record AgentSessionActivityEntrySnapshot(
    AgentSessionActivityEntryKind Kind,
    string Content,
    TimeSpan Age);
