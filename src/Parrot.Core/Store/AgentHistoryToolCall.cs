namespace Parrot.Store;

internal sealed record AgentHistoryToolCall(
    string Id,
    string Name,
    string ArgumentsJson);
