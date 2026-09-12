namespace Parrot.Store;

internal sealed record AgentHistoryMessageEntry(
    long Sequence,
    long ConversationSequence,
    string Origin,
    string Role,
    IReadOnlyList<AgentHistoryPart> Parts,
    IReadOnlyList<AgentHistoryToolCall> ToolCalls,
    string ToolCallId) : AgentHistoryEntry(Sequence);
