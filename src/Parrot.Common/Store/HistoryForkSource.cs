namespace Parrot.Store;

/// <summary>Identifies a history source and its source-specific safe fork boundary.</summary>
internal sealed record HistoryForkSource(string AgentSessionId, HistoryForkBoundary Boundary);
