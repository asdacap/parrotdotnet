namespace Parrot.Store;

internal sealed record ConversationGroup(
    IReadOnlyList<ConversationItem> Items,
    long StartWatermark,
    long EndWatermark,
    long AssistantSequence,
    bool IsComplete);
