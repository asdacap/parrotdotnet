namespace Parrot.Store;

internal sealed record AgentHistoryRequestEntry(
    long Sequence,
    long EventSequence,
    string RequestId,
    string Provider,
    string Model,
    string? Effort,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    double InputCost,
    double OutputCost,
    long ToolCalls) : AgentHistoryEntry(Sequence)
{
    public double TotalCost => InputCost + OutputCost;
}
