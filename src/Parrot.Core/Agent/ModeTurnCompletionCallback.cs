namespace Parrot.Agent;

internal sealed class ModeTurnCompletionCallback : IAgentTurnCompletionCallback
{
    public ValueTask<AgentTurnCompletionOutcome> Complete(
        AgentTurnCompletionCandidate candidate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modeOutcome = candidate.Profile.Complete(candidate.SessionId, candidate.MessageId);
        return ValueTask.FromResult(modeOutcome.RepairDiagnostic is { } diagnostic
            ? AgentTurnCompletionOutcome.Retry(
                AgentTurnCompletionProjection.PlanValidationRepair,
                diagnostic,
                true,
                true,
                true,
                true,
                false)
            : AgentTurnCompletionOutcome.Continue(null, modeOutcome.Completion));
    }
}
