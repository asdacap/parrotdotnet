namespace Parrot.Store;

internal sealed record EffectiveConversationHistory(
    CompactionSnapshot? Snapshot,
    ConversationItem? Status,
    IReadOnlyList<ConversationGroup> Groups);
