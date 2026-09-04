using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed record AgentLaunchRequest(
    IAgentSession Parent,
    AgentTurnSelection Selection,
    string RequestedProfile,
    ModelSelector Model,
    string RequestedName,
    string RequestedScope,
    HistoryForkSelection Fork,
    long AssistantSequence,
    string SpawnToolCallId,
    AgentCompletionDeliveryPolicy DeliveryPolicy);
