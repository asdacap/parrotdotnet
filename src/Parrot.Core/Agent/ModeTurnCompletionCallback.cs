using Parrot.Events;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class ModeTurnCompletionCallback(
    IEventRepository eventRepository,
    IEventBroker eventBroker) : IAgentTurnCompletionCallback
{
    public async ValueTask<AgentTurnCompletionOutcome> Complete(
        AgentTurnCompletionCandidate candidate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modeOutcome = candidate.Mode.Complete();
        if (modeOutcome.RepairDiagnostic is not { } diagnostic)
        {
            if (modeOutcome.Completion is { } completion)
            {
                completion.AgentSessionId = candidate.SessionId;
                completion.MessageId = candidate.MessageId;
            }

            return AgentTurnCompletionOutcome.Continue(null, modeOutcome.Completion);
        }

        var published = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = candidate.SessionId,
            PlanValidationRepairInjected = new PlanValidationRepairInjected { Diagnostic = diagnostic },
        };
        eventRepository.AppendPlanValidationRepair(published, candidate.AssistantText, diagnostic);
        await eventBroker.PublishWithCancellation(published, cancellationToken).ConfigureAwait(false);
        return AgentTurnCompletionOutcome.Retry(diagnostic, true, true, true, true);
    }
}
