using Parrot.Events;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class ActiveWorkTurnCompletionCallback(
    ActiveWorkCompletionReminder activeWorkReminder,
    EventRepository eventRepository,
    EventBroker eventBroker) : IAgentTurnCompletionCallback
{
    public async ValueTask<AgentTurnCompletionOutcome> Complete(
        AgentTurnCompletionCandidate candidate,
        CancellationToken cancellationToken)
    {
        var reminder = candidate.Profile.EnforceActiveWorkCompletion ? activeWorkReminder.Build() : null;
        if (reminder is null)
        {
            return AgentTurnCompletionOutcome.Continue(null, null);
        }

        var published = new Event { Id = Identifier.EventId(), AgentSessionId = candidate.SessionId };
        eventRepository.AppendActiveWorkReminder(published, reminder);
        await eventBroker.Publish(published, cancellationToken).ConfigureAwait(false);
        return AgentTurnCompletionOutcome.Retry(reminder, false, false, false, null);
    }
}
