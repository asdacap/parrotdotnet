using Parrot.Llm;
using Parrot.Protocol;

namespace Parrot.Agent;

internal sealed partial class AgentSession
{
    public AgentSessionStatisticsSnapshot CaptureStatistics() => _statistics.Capture();

    public void AddDescendantUsage(AgentUsageIncrement increment)
    {
        _statistics.AddDescendant(increment);
        parentScope.Parent?.Session.AddDescendantUsage(increment);
    }

    private void AddOwnUsage(AgentUsageIncrement increment)
    {
        _statistics.AddOwn(increment);
        parentScope.Parent?.Session.AddDescendantUsage(increment);
    }

    private void RecordRequestUsage(ProviderModel selectedModel, ReasoningOptions? reasoning, LLMEvent completed)
    {
        var increment = AgentUsageIncrement.FromCompletion(selectedModel, reasoning, completed);
        var request = new RequestUsageRecorded
        {
            RequestId = Identifier.EventId(),
            ParentAgentSessionId = ParentSessionId,
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
            AgentSessionId = SessionId,
            RequestUsageRecorded = request,
        };
        RecordUsage(fact, increment);
    }

    private void RecordToolUsage(long assistantSequence, LLMToolCall call)
    {
        var request = eventRepository.FindRequestUsage(SessionId, call.Id);
        var key = request is null
            ? AgentUsageKey.Legacy
            : new AgentUsageKey(request.Provider, request.Model, request.HasEffort ? request.Effort : null);
        var execution = new ToolExecutionStarted
        {
            RequestId = request?.RequestId ?? string.Empty,
            AssistantSequence = assistantSequence,
            ToolCallId = call.Id,
            ToolName = call.Name,
            ParentAgentSessionId = ParentSessionId,
            Provider = key.Provider,
            Model = key.Model,
        };
        if (key.Effort is { } effort)
        {
            execution.Effort = effort;
        }

        var fact = new Event
        {
            Id = $"tool-usage:{SessionId}:{call.Id}",
            AgentSessionId = SessionId,
            ToolExecutionStarted = execution,
        };
        RecordUsage(fact, AgentUsageIncrement.FromToolStart(key));
    }

    private void RecordUsage(Event fact, AgentUsageIncrement increment)
    {
        IAgentSession root = this;
        var ancestor = parentScope.Parent;
        while (ancestor is not null)
        {
            root = ancestor.Session;
            ancestor = ancestor.ParentScope.Parent;
        }

        userStatistics.RecordUsage(eventRepository, eventBroker, fact, increment, AddOwnUsage, this, root);
    }
}
