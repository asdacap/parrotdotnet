using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

// Records an agent's provider and tool usage as durable facts and rolls each
// increment up to its ancestors.
internal sealed class AgentUsageRecorder(
    AgentIdentity identity,
    IAgentParentScope parentScope,
    IUserSessionStatistics userStatistics,
    AgentSessionStatistics statistics,
    IAgentHistory history,
    IEventBroker eventBroker)
{
    public void RecordRequestUsage(ProviderModel selectedModel, ReasoningOptions? reasoning, LLMEvent completed)
    {
        var increment = AgentUsageIncrement.FromCompletion(selectedModel, reasoning, completed);
        var request = new RequestUsageRecorded
        {
            RequestId = Identifier.EventId(),
            ParentAgentSessionId = identity.ParentSessionId,
            Provider = increment.Key.Provider,
            Model = increment.Key.Model,
            InputTokens = increment.Totals.InputTokens,
            CachedInputTokens = increment.Totals.CachedInputTokens,
            OutputTokens = increment.Totals.OutputTokens,
            InputCost = increment.Totals.InputCost,
            OutputCost = increment.Totals.OutputCost,
            ContextSize = increment.ContextSize.GetValueOrDefault(),
            ContextLimit = increment.ContextLimit.GetValueOrDefault(),
        };
        if (increment.Key.Effort is { } effort)
        {
            request.Effort = effort;
        }

        request.ToolCallIds.AddRange(completed.ToolCalls.Select(static call => call.Id));
        var fact = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = identity.SessionId,
            RequestUsageRecorded = request,
        };
        RecordUsage(fact, increment);
    }

    public void RecordToolUsage(long assistantSequence, LLMToolCall call)
    {
        var request = history.FindRequestUsage(call.Id);
        var key = request is null
            ? AgentUsageKey.Legacy
            : new AgentUsageKey(request.Provider, request.Model, request.HasEffort ? request.Effort : null);
        var execution = new ToolExecutionStarted
        {
            RequestId = request?.RequestId ?? string.Empty,
            AssistantSequence = assistantSequence,
            ToolCallId = call.Id,
            ToolName = call.Name,
            ParentAgentSessionId = identity.ParentSessionId,
            Provider = key.Provider,
            Model = key.Model,
        };
        if (key.Effort is { } effort)
        {
            execution.Effort = effort;
        }

        var fact = new Event
        {
            Id = $"tool-usage:{identity.SessionId}:{call.Id}",
            AgentSessionId = identity.SessionId,
            ToolExecutionStarted = execution,
        };
        RecordUsage(fact, AgentUsageIncrement.FromToolStart(key));
    }

    private void AddOwnUsage(AgentUsageIncrement increment)
    {
        statistics.AddOwn(increment);
        parentScope.Parent?.Session.AddDescendantUsage(increment);
    }

    private void RecordUsage(Event fact, AgentUsageIncrement increment)
    {
        var rootSessionId = identity.SessionId;
        var ancestor = parentScope.Parent;
        while (ancestor is not null)
        {
            rootSessionId = ancestor.Session.SessionId;
            ancestor = ancestor.ParentScope.Parent;
        }

        userStatistics.RecordUsage(
            eventBroker,
            fact,
            increment,
            AddOwnUsage,
            statistics,
            userStatistics.GetAgentStatistics(rootSessionId));
    }
}
