using Parrot.Llm;

namespace Parrot.Agent;

internal sealed record AgentTurnCompletionCandidate(
    string SessionId,
    string MessageId,
    LLMMessage Assistant,
    IAgentProfile Profile);
