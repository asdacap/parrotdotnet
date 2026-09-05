using Parrot.Events;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class ModeTurnCompletionCallback(
    EventRepository eventRepository,
    EventBroker eventBroker) : IAgentTurnCompletionCallback
{
    public async ValueTask<AgentTurnCompletionOutcome> Complete(
        AgentTurnCompletionCandidate candidate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modeOutcome = candidate.Profile.Complete(candidate.SessionId, candidate.MessageId);
        if (modeOutcome.RepairDiagnostic is not { } diagnostic)
        {
            return AgentTurnCompletionOutcome.Continue(null, modeOutcome.Completion);
        }

        var published = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = candidate.SessionId,
            PlanValidationRepairInjected = new PlanValidationRepairInjected { Diagnostic = diagnostic },
        };
        eventRepository.AppendPlanValidationRepair(published, candidate.AssistantText, diagnostic);
        await eventBroker.Publish(published, cancellationToken).ConfigureAwait(false);
        return AgentTurnCompletionOutcome.Retry(diagnostic, true, true, true, true);
    }
}
