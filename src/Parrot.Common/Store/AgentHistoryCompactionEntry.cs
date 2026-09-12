namespace Parrot.Store;

internal sealed record AgentHistoryCompactionEntry(
    long Sequence,
    string Summary,
    long Watermark) : AgentHistoryEntry(Sequence);
