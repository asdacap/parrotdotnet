namespace Parrot.Store;

internal abstract record HistoryForkBoundary
{
    private HistoryForkBoundary()
    {
    }

    public sealed record BeforeToolBatch(long AssistantSequence, string ToolCallId) : HistoryForkBoundary;

    public sealed record AfterCompletedHistory : HistoryForkBoundary;
}
