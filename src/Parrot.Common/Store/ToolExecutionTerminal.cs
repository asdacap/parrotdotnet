namespace Parrot.Store;

internal sealed record ToolExecutionTerminal(
    string ToolCallId,
    string ToolName,
    ToolExecutionStatus Status,
    IReadOnlyList<ConversationPart> ResultParts,
    string Message);
