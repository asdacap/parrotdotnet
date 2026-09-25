using Parrot.Agent;
using Parrot.Context;
using Parrot.Llm;
using Parrot.Protocol;

namespace Parrot.Store;

/// <summary>Commits events and maintains their durable session projections.</summary>
internal interface IEventRepository
{
    IUserSessionStatistics GetRuntimeStatistics();

    IEventRepository BindAgentHistory(IAgentHistoryFile historyFile);

    void RefreshAgentHistory(string agentSessionId);

    SessionUsage? Append(Event published, string? messageRole, string? messageContent);

    SessionUsage? AppendMessage(Event published, LLMMessage? message, ConversationOrigin origin);

    void AppendConversation(
        Event published,
        ConversationOrigin origin,
        LLMRole role,
        IReadOnlyList<ConversationPart> parts,
        IReadOnlyList<LLMToolCall> toolCalls,
        string toolCallId);

    bool AppendToolResult(
        Event published,
        long assistantSequence,
        ToolExecutionTerminal terminal);

    bool HasToolSynthetic(long assistantSequence, string agentSessionId);

    bool AppendToolSynthetic(
        Event published,
        long assistantSequence,
        IReadOnlyList<ConversationPart> imageParts);

    Admission Admit(
        string agentSessionId,
        string messageId,
        string content,
        Delivery delivery,
        Func<AdmittedInput, Event> compose);

    Admission AdmitParts(
        string agentSessionId,
        string messageId,
        IReadOnlyList<ConversationPart> parts,
        Delivery delivery,
        Func<AdmittedInput, Event> compose);

    Admission? AdmitSteerIfIdle(
        string agentSessionId,
        string messageId,
        string content,
        Func<AdmittedInput, Event> compose);

    Event? CancelPendingInput(string agentSessionId, string inputId, Func<Event> compose);

    IReadOnlyList<Promotion> PromoteSteers(string agentSessionId, Func<AdmittedInput, Event> compose);

    IReadOnlyList<Promotion> PromoteNextQueue(string agentSessionId, Func<AdmittedInput, Event> compose);

    IReadOnlyList<AdmittedInput> InputsForNextPromotion(string agentSessionId);

    bool HasPendingInputs(string agentSessionId);

    IReadOnlyList<Event> Replay();

    SessionUsage Usage();

    AgentStatistics? LatestStatistics(string agentSessionId);

    IReadOnlyList<ExitReminderEntry> ExitReminders(string agentSessionId);

    void AppendExitReminderChanged(Event published, string title, string? description);

    void AppendExitReminder(Event published, string assistantContent, string renderedReminder);

    IReadOnlyList<string> Messages(string agentSessionId);

    IReadOnlyList<LLMMessage> ModelHistory(string agentSessionId);

    IReadOnlyList<LLMContent> Materialize(IReadOnlyList<ConversationPart> parts);

    IReadOnlyList<ConversationItem> Conversation(string agentSessionId);

    IReadOnlyList<string> AgentHistorySessionIds();

    /// <summary>Lists every agent that ever started, by its first start, with its parent and name.</summary>
    IReadOnlyList<AgentLineageRecord> AgentLineage();

    IReadOnlyList<AgentHistoryEntry> AgentHistory(string agentSessionId);

    bool RecordCheckpoint(string agentSessionId, string title, long assistantSequence, string toolCallId);

    HistoryCheckpoint? LatestUsableCheckpoint(string agentSessionId, string title, long beforeAssistantSequence);

    IReadOnlySet<long> ActiveCheckpointAssistantSequences(string agentSessionId);

    EffectiveConversationHistory EffectiveConversationGroups(string agentSessionId);

    void InitializeForkedAgentHistory(
        string sourceAgentSessionId,
        string destinationAgentSessionId,
        HistoryForkBoundary boundary,
        HistoryForkSelection selection);

    void CleanupForkedAgentHistory(string agentSessionId);

    IReadOnlyList<ConversationItem> ConversationAfter(string agentSessionId, long watermark);

    bool AppendToolTerminal(Event published, ToolExecutionTerminal terminal);

    bool AppendToolSettlement(
        Event published,
        long assistantSequence,
        ToolExecutionTerminal terminal);

    IReadOnlyList<ToolExecutionTerminal> ToolTerminals(string agentSessionId);

    bool SaveCompaction(string agentSessionId, CompactionSnapshot snapshot);

    bool AppendCompactionStatus(Event publishedStatus, CompactionSnapshot snapshot, string content);

    CompactionSnapshot? Compaction(string agentSessionId);

    CompactionContext? CompactionHistory(string agentSessionId);

    (string AgentSessionId, string Mode) SessionState(string userSessionId, string requestedMode);

    void UpdateMode(string userSessionId, string agentSessionId, string mode);

    bool StatusPromptPending(string agentSessionId);

    PendingStatus? PendingStatus(string agentSessionId);

    bool AppendStatusPrompt(Event published, PendingStatus expected, string content);

    void AppendInitialStatusPrompt(Event published, string content);

    void AppendPlanValidationRepair(Event published, string assistantContent, string diagnostic);

    void AppendPendingChildQuestionReminder(
        Event published,
        string assistantContent,
        string reminder);

    void AppendActiveWorkReminder(Event published, string content);

    ContextReminderCheckpoint? LatestContextReminder(string agentSessionId);

    bool AppendContextReminder(
        Event published,
        ContextReminderCheckpoint checkpoint,
        int usagePercent,
        string content);

    void AppendFinalProviderRequestPrompt(Event published, string content);

    bool AppendToolAvailabilityRestoredPrompt(Event published, string content);

    ImageArtifactMetadata RecordImageArtifact(ImageArtifactMetadata artifact, string uploadId);

    ImageArtifactMetadata? ResolveImageArtifact(string artifactId);

    void ClaimImageArtifact(string artifactId, string referenceId);

    void ReleaseImageArtifact(string artifactId, string referenceId);

    IReadOnlyList<ImageArtifactMetadata> RemoveStaleUnreferencedImageArtifacts(DateTimeOffset before);

    long AppendUsageFact(Event published);

    RequestUsageRecorded? FindRequestUsage(string agentSessionId, string toolCallId);

    AgentStatisticsReplay ReplayStatistics();
}
