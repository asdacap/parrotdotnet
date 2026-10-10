using Parrot.Agent;
using Parrot.Context;
using Parrot.Llm;
using Parrot.Protocol;

namespace Parrot.Store;

// One agent's view of the session repository. The repository it wraps is the
// one bound to the agent's history file, so every write keeps that file in step.
internal sealed class AgentHistoryView(EventRepository repository, string agentSessionId) : IAgentHistory
{
    public SessionUsage? Append(Event published, string? messageRole, string? messageContent) =>
        repository.Append(published, messageRole, messageContent);

    public SessionUsage? AppendMessage(Event published, LLMMessage? message, ConversationOrigin origin) =>
        repository.AppendMessage(published, message, origin);

    public void AppendConversation(
        Event published,
        ConversationOrigin origin,
        LLMRole role,
        IReadOnlyList<ConversationPart> parts,
        IReadOnlyList<LLMToolCall> toolCalls,
        string toolCallId) =>
        repository.AppendConversation(published, origin, role, parts, toolCalls, toolCallId);

    public bool HasToolSynthetic(long assistantSequence) =>
        repository.HasToolSynthetic(assistantSequence, agentSessionId);

    public ConversationItem? AppendToolSynthetic(
        Event published,
        long assistantSequence,
        IReadOnlyList<ConversationPart> imageParts) =>
        repository.AppendToolSynthetic(published, assistantSequence, imageParts);

    public ConversationItem? AppendToolSettlement(
        Event published,
        long assistantSequence,
        ToolExecutionTerminal terminal) =>
        repository.AppendToolSettlement(published, assistantSequence, terminal);

    public IReadOnlyList<ToolExecutionTerminal> ToolTerminalsOfCalls(IReadOnlyCollection<string> toolCallIds) =>
        repository.ToolTerminalsOfCalls(agentSessionId, toolCallIds);

    public Admission AdmitParts(
        string messageId,
        IReadOnlyList<ConversationPart> parts,
        Delivery delivery,
        Func<AdmittedInput, Event> compose) =>
        repository.AdmitParts(agentSessionId, messageId, parts, delivery, compose);

    public Event? CancelPendingInput(string inputId, Func<Event> compose) =>
        repository.CancelPendingInput(agentSessionId, inputId, compose);

    public IReadOnlyList<Promotion> PromoteSteers(Func<AdmittedInput, Event> compose) =>
        repository.PromoteSteers(agentSessionId, compose);

    public IReadOnlyList<Promotion> PromoteNextQueue(Func<AdmittedInput, Event> compose) =>
        repository.PromoteNextQueue(agentSessionId, compose);

    public IReadOnlyList<AdmittedInput> InputsForNextPromotion() =>
        repository.InputsForNextPromotion(agentSessionId);

    public bool HasPendingInputs() => repository.HasPendingInputs(agentSessionId);

    public IReadOnlyList<ExitReminderEntry> ExitReminders() => repository.ExitReminders(agentSessionId);

    public void AppendExitReminderChanged(Event published, string title, string? description) =>
        repository.AppendExitReminderChanged(published, title, description);

    public void AppendExitReminder(Event published, string assistantContent, string renderedReminder) =>
        repository.AppendExitReminder(published, assistantContent, renderedReminder);

    public IReadOnlyList<string> Messages() => repository.Messages(agentSessionId);

    public IReadOnlyList<LLMContent> Materialize(IReadOnlyList<ConversationPart> parts) =>
        repository.Materialize(parts);

    public ImageArtifactMetadata? ResolveImageArtifact(string artifactId) =>
        repository.ResolveImageArtifact(artifactId);

    public IReadOnlyList<ConversationItem> Conversation() => repository.Conversation(agentSessionId);

    public IReadOnlyList<ConversationItem> ConversationAfter(long watermark) =>
        repository.ConversationAfter(agentSessionId, watermark);

    public bool RecordCheckpoint(string title, long assistantSequence, string toolCallId) =>
        repository.RecordCheckpoint(agentSessionId, title, assistantSequence, toolCallId);

    public IReadOnlySet<long> ActiveCheckpointAssistantSequences() =>
        repository.ActiveCheckpointAssistantSequences(agentSessionId);

    public EffectiveConversationHistory EffectiveConversationGroups() =>
        repository.EffectiveConversationGroups(agentSessionId);

    public CompactionContext? CompactionHistory() => repository.CompactionHistory(agentSessionId);

    public bool AppendCompactionStatus(Event publishedStatus, CompactionSnapshot snapshot, string content) =>
        repository.AppendCompactionStatus(publishedStatus, snapshot, content);

    public PendingStatus? PendingStatus() => repository.PendingStatus(agentSessionId);

    public bool AppendStatusPrompt(Event published, PendingStatus expected, string content) =>
        repository.AppendStatusPrompt(published, expected, content);

    public void AppendSystemPrompt(Event published, string content) =>
        repository.AppendSystemPrompt(published, content);

    public void AppendPendingChildQuestionReminder(Event published, string assistantContent, string reminder) =>
        repository.AppendPendingChildQuestionReminder(published, assistantContent, reminder);

    public ContextReminderCheckpoint? LatestContextReminder() => repository.LatestContextReminder(agentSessionId);

    public bool AppendContextReminder(
        Event published,
        ContextReminderCheckpoint checkpoint,
        int usagePercent,
        string content) =>
        repository.AppendContextReminder(published, checkpoint, usagePercent, content);

    public void AppendFinalProviderRequestPrompt(Event published, string content) =>
        repository.AppendFinalProviderRequestPrompt(published, content);

    public bool AppendToolAvailabilityRestoredPrompt(Event published, string content) =>
        repository.AppendToolAvailabilityRestoredPrompt(published, content);

    public RequestUsageRecorded? FindRequestUsage(string toolCallId) =>
        repository.FindRequestUsage(agentSessionId, toolCallId);

    public void InitializeFork(
        string sourceAgentSessionId,
        HistoryForkBoundary boundary,
        HistoryForkSelection selection,
        HistoryForkSource? fallback) =>
        repository.InitializeForkedAgentHistory(sourceAgentSessionId, agentSessionId, boundary, selection, fallback);

    public void CleanupFork() => repository.CleanupForkedAgentHistory(agentSessionId);

    public void Refresh() => repository.RefreshAgentHistory(agentSessionId);
}
