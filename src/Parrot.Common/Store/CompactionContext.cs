namespace Parrot.Store;

internal sealed record CompactionContext(
    CompactionSnapshot Snapshot,
    ConversationItem? Status,
    IReadOnlyList<ConversationItem> Tail);
