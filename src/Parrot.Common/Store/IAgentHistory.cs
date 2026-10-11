using Parrot.Context;
using Parrot.Llm;
using Parrot.Protocol;

namespace Parrot.Store;

/// <summary>Commits one agent session's events and reads its durable conversation, inputs and reminders.</summary>
internal interface IAgentHistory
{
    SessionUsage? Append(Event published, string? messageRole, string? messageContent);

    SessionUsage? AppendMessage(Event published, LLMMessage? message, ConversationOrigin origin);

    void AppendConversation(
        Event published,
        ConversationOrigin origin,
        LLMRole role,
        IReadOnlyList<ConversationPart> parts,
        IReadOnlyList<LLMToolCall> toolCalls,
        LLMProviderContent providerContent,
        string toolCallId);

    bool HasToolSynthetic(long assistantSequence);

    /// <summary>Projects a batch's image message, returning it or null when one was already projected.</summary>
    ConversationItem? AppendToolSynthetic(
        Event published,
        long assistantSequence,
        IReadOnlyList<ConversationPart> imageParts);

    /// <summary>Settles a tool call, returning the tool result it projected or null when one was already projected.</summary>
    ConversationItem? AppendToolSettlement(
        Event published,
        long assistantSequence,
        ToolExecutionTerminal terminal);

    /// <summary>Lists the terminals of the given tool calls that have settled; an unsettled call has none.</summary>
    IReadOnlyList<ToolExecutionTerminal> ToolTerminalsOfCalls(IReadOnlyCollection<string> toolCallIds);

    Admission AdmitParts(
        string messageId,
        IReadOnlyList<ConversationPart> parts,
        Delivery delivery,
        Func<AdmittedInput, Event> compose);

    Event? CancelPendingInput(string inputId, Func<Event> compose);

    IReadOnlyList<Promotion> PromoteSteers(Func<AdmittedInput, Event> compose);

    IReadOnlyList<Promotion> PromoteNextQueue(Func<AdmittedInput, Event> compose);

    IReadOnlyList<AdmittedInput> InputsForNextPromotion();

    bool HasPendingInputs();

    /// <summary>Lists this agent's recorded notices of the given kind in commit order.</summary>
    IReadOnlyList<AgentNotice> Notices(string kind);

    IReadOnlyList<string> Messages();

    IReadOnlyList<LLMContent> Materialize(IReadOnlyList<ConversationPart> parts);

    ImageArtifactMetadata? ResolveImageArtifact(string artifactId);

    IReadOnlyList<ConversationItem> Conversation();

    IReadOnlyList<ConversationItem> ConversationAfter(long watermark);

    bool RecordCheckpoint(string title, long assistantSequence, string toolCallId);

    IReadOnlySet<long> ActiveCheckpointAssistantSequences();

    EffectiveConversationHistory EffectiveConversationGroups();

    CompactionContext? CompactionHistory();

    bool AppendCompactionStatus(Event publishedStatus, CompactionSnapshot snapshot, string content);

    PendingStatus? PendingStatus();

    bool AppendStatusPrompt(Event published, PendingStatus expected, string content);

    /// <summary>Projects a system prompt for an event whose injection payload the caller has already set.</summary>
    void AppendSystemPrompt(Event published, string content);

    /// <summary>Records the event and projects the retained assistant response followed by a system reminder.</summary>
    void AppendRetainedReminder(
        Event published,
        LLMMessage assistant,
        string reminder);

    ContextReminderCheckpoint? LatestContextReminder();

    bool AppendContextReminder(
        Event published,
        ContextReminderCheckpoint checkpoint,
        string content);

    void AppendFinalProviderRequestPrompt(Event published, string content);

    bool AppendToolAvailabilityRestoredPrompt(Event published, string content);

    RequestUsageRecorded? FindRequestUsage(string toolCallId);

    /// <summary>Atomically forks history into this agent, using the fallback only when the primary has no effective history.</summary>
    void InitializeFork(
        string sourceAgentSessionId,
        HistoryForkBoundary boundary,
        HistoryForkSelection selection,
        HistoryForkSource? fallback);

    void CleanupFork();

    /// <summary>Brings this agent's on-disk history projection up to date.</summary>
    void Refresh();
}
