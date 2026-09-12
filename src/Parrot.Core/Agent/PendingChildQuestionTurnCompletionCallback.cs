using Parrot.Events;
using Parrot.Protocol;
using Parrot.Questions;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class PendingChildQuestionTurnCompletionCallback(
    IChildQuestionCoordinator childQuestions,
    IEventRepository eventRepository,
    IEventBroker eventBroker) : IAgentTurnCompletionCallback
{
    public async ValueTask<AgentTurnCompletionOutcome> Complete(
        AgentTurnCompletionCandidate candidate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ChildQuestionCompletionAttempt? completionAttempt = null;
        try
        {
            completionAttempt = childQuestions.BeginCompletion();
            if (completionAttempt.Reminder is { } reminder)
            {
                var published = new Event { Id = Identifier.EventId(), AgentSessionId = candidate.SessionId };
                eventRepository.AppendPendingChildQuestionReminder(published, candidate.AssistantText, reminder);
                await eventBroker.PublishWithCancellation(published, cancellationToken).ConfigureAwait(false);
                return AgentTurnCompletionOutcome.Retry(reminder, true, false, true, null);
            }

            var outcome = AgentTurnCompletionOutcome.Continue(completionAttempt, null);
            completionAttempt = null;
            return outcome;
        }
        finally
        {
            completionAttempt?.Dispose();
        }
    }
}
