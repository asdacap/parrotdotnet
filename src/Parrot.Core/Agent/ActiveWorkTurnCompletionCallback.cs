namespace Parrot.Agent;

internal sealed class ActiveWorkTurnCompletionCallback(
    ActiveWorkCompletionReminder activeWorkReminder) : IAgentTurnCompletionCallback
{
    public ValueTask<AgentTurnCompletionOutcome> Complete(
        AgentTurnCompletionCandidate candidate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reminder = candidate.Profile.EnforceActiveWorkCompletion
            ? activeWorkReminder.Build()
            : null;
        return ValueTask.FromResult(reminder is null
            ? AgentTurnCompletionOutcome.Continue(null, null)
            : AgentTurnCompletionOutcome.Retry(
                AgentTurnCompletionProjection.ActiveWorkReminder,
                reminder,
                false,
                false,
                false,
                null,
                false));
    }
}
