using Parrot.Context;
using Parrot.Llm;

namespace Parrot.Agent;

internal interface IAgentSessionContext
{
    ContextSnapshot EstimateContext(AgentTurnSelection selection);

    IReadOnlyList<LLMToolDefinition> AdvertisedToolDefinitions(AgentTurnSelection selection);

    ContextSnapshot EstimateContextForTools(
        AgentTurnSelection selection,
        IReadOnlyList<LLMToolDefinition> tools);

    ContextSnapshot EstimateContextAfterToolResult(
        AgentTurnSelection selection,
        string toolCallId,
        string result);

    Task<ContextCompactionResult> CompactFromTool(
        AgentTurnSelection selection,
        CancellationToken cancellationToken);
}
