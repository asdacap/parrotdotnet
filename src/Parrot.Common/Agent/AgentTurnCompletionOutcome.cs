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
        bool selectCandidateAnswer,
        bool recordAssistantActivity,
        bool? completionRetryPending) =>
        new RetryOutcome(
            systemMessage,
            retainCandidateAssistant,
            selectCandidateAnswer,
            recordAssistantActivity,
            completionRetryPending);

    internal sealed record ContinueOutcome(IDisposable? CompletionReservation) : AgentTurnCompletionOutcome;

    internal sealed record RetryOutcome(
        string SystemMessage,
        bool RetainCandidateAssistant,
        bool SelectCandidateAnswer,
        bool RecordAssistantActivity,
        bool? CompletionRetryPending) : AgentTurnCompletionOutcome;
}
