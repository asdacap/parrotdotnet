namespace Parrot.Agent;

internal abstract record AgentTurnCompletionOutcome
{
    private AgentTurnCompletionOutcome()
    {
    }

    internal static AgentTurnCompletionOutcome Continue() => new ContinueOutcome();

    internal static AgentTurnCompletionOutcome Retry(
        string systemMessage,
        bool retainCandidateAssistant,
        bool? completionRetryPending) =>
        new RetryOutcome(systemMessage, retainCandidateAssistant, completionRetryPending);

    internal sealed record ContinueOutcome : AgentTurnCompletionOutcome;

    internal sealed record RetryOutcome(
        string SystemMessage,
        bool RetainCandidateAssistant,
        bool? CompletionRetryPending) : AgentTurnCompletionOutcome;
}
