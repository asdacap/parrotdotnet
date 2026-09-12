namespace Parrot.Store;

internal sealed record HistoryCheckpoint(string Title, long AssistantSequence, string ToolCallId);
