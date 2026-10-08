namespace Parrot.Agent;

internal abstract record AgentTurnCompletionOutcome
{
    private AgentTurnCompletionOutcome()
    {
    }

    internal static AgentTurnCompletionOutcome Continue(IDisposable? completionReservation) =>
        new ContinueOutcome(completionReservation);

    internal static AgentTurnCompletionOutcome Retry(
        string systemMessage,
        bool retainCandidateAssistant,
        bool? completionRetryPending) =>
        new RetryOutcome(systemMessage, retainCandidateAssistant, completionRetryPending);

    internal sealed record ContinueOutcome(IDisposable? CompletionReservation) : AgentTurnCompletionOutcome;

    internal sealed record RetryOutcome(
        string SystemMessage,
        bool RetainCandidateAssistant,
        bool? CompletionRetryPending) : AgentTurnCompletionOutcome;
}
