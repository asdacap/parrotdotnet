using Parrot.Events;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class ExitReminderTurnCompletionCallback(
    ExitReminder exitReminder,
    EventRepository eventRepository,
    EventBroker eventBroker) : IAgentTurnCompletionCallback
{
    public async ValueTask<AgentTurnCompletionOutcome> Complete(
        AgentTurnCompletionCandidate candidate,
        CancellationToken cancellationToken)
    {
        var reminder = exitReminder.Build();
        if (reminder is null)
        {
            return AgentTurnCompletionOutcome.Continue(null, null);
        }

        var published = new Event { Id = Identifier.EventId(), AgentSessionId = candidate.SessionId };
        eventRepository.AppendExitReminder(published, candidate.AssistantText, reminder);
        await eventBroker.Publish(published, cancellationToken).ConfigureAwait(false);
        return AgentTurnCompletionOutcome.Retry(reminder, true, false, true, true, true);
    }
}
