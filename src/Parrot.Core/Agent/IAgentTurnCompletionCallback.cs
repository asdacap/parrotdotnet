namespace Parrot.Agent;

internal interface IAgentTurnCompletionCallback
{
    ValueTask<AgentTurnCompletionOutcome> Complete(
        AgentTurnCompletionCandidate candidate,
        CancellationToken cancellationToken);
}
