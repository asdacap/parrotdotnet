using Parrot.Events;
using Parrot.Protocol;
using Parrot.Questions;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class PendingChildQuestionTurnCompletionCallback(
    IChildQuestionCoordinator childQuestions,
    IAgentHistory history,
    IEventBroker eventBroker) : IAgentTurnCompletionCallback
{
    private const string NoticeKind = "pending_child_question_reminder";

    public async ValueTask<AgentTurnCompletionOutcome> Complete(
        AgentTurnCompletionCandidate candidate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (childQuestions.CompletionReminder() is not { } reminder)
        {
            return AgentTurnCompletionOutcome.Continue();
        }

        var published = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = candidate.SessionId,
            AgentNotice = new AgentNotice { Kind = NoticeKind, Notice = "Retrying with pending child question reminder" },
        };
        history.AppendRetainedReminder(published, candidate.AssistantText, reminder);
        await eventBroker.PublishWithCancellation(published, cancellationToken).ConfigureAwait(false);
        return AgentTurnCompletionOutcome.Retry(reminder, true, true);
    }
}
