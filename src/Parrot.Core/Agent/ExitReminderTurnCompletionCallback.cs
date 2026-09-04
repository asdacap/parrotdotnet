namespace Parrot.Agent;

internal sealed class ExitReminderTurnCompletionCallback(
    ExitReminder exitReminder) : IAgentTurnCompletionCallback
{
    public ValueTask<AgentTurnCompletionOutcome> Complete(
        AgentTurnCompletionCandidate candidate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reminder = exitReminder.Build();
        return ValueTask.FromResult(reminder is null
            ? AgentTurnCompletionOutcome.Continue(null, null)
            : AgentTurnCompletionOutcome.Retry(
                AgentTurnCompletionProjection.ExitReminder,
                reminder,
                true,
                false,
                true,
                true,
                true));
    }
}
