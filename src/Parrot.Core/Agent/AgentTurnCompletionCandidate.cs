namespace Parrot.Agent;

internal sealed record AgentTurnCompletionCandidate(
    string SessionId,
    string MessageId,
    string AssistantText,
    IMode Profile);
