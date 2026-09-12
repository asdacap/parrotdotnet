namespace Parrot.Agent;

internal sealed record AgentSessionActivitySnapshot(
    DrainState State,
    string? CurrentTool,
    TimeSpan? RequestSessionDuration,
    TimeSpan? CurrentProviderRequestDuration,
    TimeSpan? LastProviderRequestDuration,
    TimeSpan? LatestProviderActivityAge,
    AgentExecution? TerminalOutcome,
    IReadOnlyList<AgentSessionActivityEntrySnapshot> Recent);
