namespace Parrot.Agent;

/// <summary>Evaluates a proposed turn completion before the session commits it.</summary>
internal interface IAgentTurnCompletionCallback
{
    /// <summary>Returns permission to continue completion, possibly with deferred work, or requests another turn cycle.</summary>
    ValueTask<AgentTurnCompletionOutcome> Complete(
        AgentTurnCompletionCandidate candidate,
        CancellationToken cancellationToken);
}
