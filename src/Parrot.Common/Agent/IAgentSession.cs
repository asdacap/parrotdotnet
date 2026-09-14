using Parrot.Context;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

/// <summary>Owns an agent's selection, admitted work, context and incoming activity for its lifetime.</summary>
internal interface IAgentSession : IAsyncDisposable
{
    string SessionId { get; }

    string Name { get; }

    string ParentSessionId { get; }

    string ParentSessionName { get; }

    int Depth { get; }

    AgentIdentity Identity { get; }

    AgentSessionActivity Activity { get; }

    /// <summary>Captures immutable own and subtree usage, including model/effort buckets and costs.</summary>
    AgentSessionStatisticsSnapshot CaptureStatistics();

    /// <summary>Accumulates committed descendant usage and forwards it to the direct parent.</summary>
    void AddDescendantUsage(AgentUsageIncrement increment);

    AgentSelection CurrentSelection();

    /// <summary>Caches the resolved model for the current selection.</summary>
    void UseResolvedSelection(ResolvedModelSelection selectedModel);

    /// <summary>Wakes the session to resume durable pending work.</summary>
    void Recover();

    void UpdateSelection(ModelSelector selectedModel, IMode mode);

    /// <summary>Offers incoming activity to the drain and reports whether a follow-up was scheduled.</summary>
    bool Wake(IncomingActivity? activity);

    /// <summary>Interrupts current work and waits for the drain to unwind.</summary>
    Task Interrupt(CancellationToken cancellationToken);

    /// <summary>Queues compaction within the session drain, using a one-off target or the selected policy when null.</summary>
    Task Compact(ContextSize? targetContextSize, CancellationToken cancellationToken);

    /// <summary>Waits for the captured drain result without admitting or waking work.</summary>
    Task Settled();

    /// <summary>Resolves the current selection's security policy through its ancestor lineage.</summary>
    AgentSelection ResolvePolicySelection();

    /// <summary>Returns the session's ancestor policy lineage.</summary>
    AgentPolicyLineage ResolvePolicyLineage();

    bool IsIdle();

    bool IsActive();

    /// <summary>Waits for incoming activity up to the supplied duration, returning null on timeout.</summary>
    Task<IncomingActivity?> WaitForIncomingInput(
        TimeSpan duration,
        TimeProvider timeProvider,
        CancellationToken cancellationToken);

    /// <summary>Durably admits input, offering the supplied reason to the session's incoming activity, and reports its admission and whether a follow-up was scheduled.</summary>
    Task<(Admission Admission, bool FollowUp)> Send(
        IReadOnlyList<ConversationPart> parts,
        string messageId,
        Delivery delivery,
        IncomingActivity reason,
        CancellationToken cancellationToken);

    void SetExitReminder(string? reminder);

    Task<AgentSendResult> SendTextMessage(string message, CancellationToken cancellationToken);

    Task<string> SendAndWaitForResult(string prompt, CancellationToken cancellationToken);

    Task ReceiveAgentTaskCompletion(
        string runId,
        string message,
        string messageId,
        CancellationToken cancellationToken);

    /// <summary>Records task completion in history without delivering incoming activity.</summary>
    Task RecordAgentTaskCompletion(
        string message,
        string messageId,
        CancellationToken cancellationToken);

    /// <summary>Waits for the captured execution, yielding a running result when the duration elapses.</summary>
    Task<WaitAgentResult> Wait(
        int yieldAfterMilliseconds,
        CancellationToken cancellationToken);

    ContextSnapshot EstimateContext(AgentTurnSelection selection);

    /// <summary>Materializes tools allowed by the captured selection.</summary>
    IReadOnlyList<LLMToolDefinition> AdvertisedToolDefinitions(AgentTurnSelection selection);

    ContextSnapshot EstimateContextForTools(
        AgentTurnSelection selection,
        IReadOnlyList<LLMToolDefinition> tools);

    /// <summary>Estimates context with a prospective tool result without persisting that result.</summary>
    ContextSnapshot EstimateContextAfterToolResult(
        AgentTurnSelection selection,
        string toolCallId,
        string result);

    /// <summary>Compacts inline with a one-off target (null uses selected policy) and reports the resulting estimate.</summary>
    Task<ContextCompactionResult> CompactFromTool(
        AgentTurnSelection selection,
        ContextSize? targetContextSize,
        CancellationToken cancellationToken);
}
