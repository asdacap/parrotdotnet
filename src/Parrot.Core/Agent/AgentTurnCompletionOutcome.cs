using Parrot.Protocol;

namespace Parrot.Agent;

internal abstract record AgentTurnCompletionOutcome
{
    private AgentTurnCompletionOutcome()
    {
    }

    internal static AgentTurnCompletionOutcome Continue(
        IDisposable? completionReservation,
        PlanCompleted? deferredPlanCompletion) =>
        new ContinueOutcome(completionReservation, deferredPlanCompletion);

    internal static AgentTurnCompletionOutcome Retry(
        string systemMessage,
        bool retainCandidateAssistant,
        bool selectCandidateAnswer,
        bool recordAssistantActivity,
        bool? completionRetryPending,
        bool resetProviderRequestBudget) =>
        new RetryOutcome(
            systemMessage,
            retainCandidateAssistant,
            selectCandidateAnswer,
            recordAssistantActivity,
            completionRetryPending,
            resetProviderRequestBudget);

    internal sealed record ContinueOutcome(
        IDisposable? CompletionReservation,
        PlanCompleted? DeferredPlanCompletion) : AgentTurnCompletionOutcome;

    internal sealed record RetryOutcome(
        string SystemMessage,
        bool RetainCandidateAssistant,
        bool SelectCandidateAnswer,
        bool RecordAssistantActivity,
        bool? CompletionRetryPending,
        bool ResetProviderRequestBudget) : AgentTurnCompletionOutcome;
}
