using Parrot.Questions;

namespace Parrot.Agent;

internal sealed class PendingChildQuestionTurnCompletionCallback(
    ChildQuestionCoordinator childQuestions) : IAgentTurnCompletionCallback
{
    public ValueTask<AgentTurnCompletionOutcome> Complete(
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
                return ValueTask.FromResult(AgentTurnCompletionOutcome.Retry(
                    AgentTurnCompletionProjection.PendingChildQuestionReminder,
                    reminder,
                    true,
                    false,
                    true,
                    null,
                    false));
            }

            var outcome = AgentTurnCompletionOutcome.Continue(completionAttempt, null);
            completionAttempt = null;
            return ValueTask.FromResult(outcome);
        }
        finally
        {
            completionAttempt?.Dispose();
        }
    }
}
