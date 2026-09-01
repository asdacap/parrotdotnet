namespace Parrot.Agent;

internal sealed record AgentSessionActivitySnapshot(
    DrainState State,
    string? CurrentTool,
    TimeSpan? LatestProviderActivityAge,
    AgentExecution? TerminalOutcome,
    IReadOnlyList<AgentSessionActivityEntrySnapshot> Recent);
