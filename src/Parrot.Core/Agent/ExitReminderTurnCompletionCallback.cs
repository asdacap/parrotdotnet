using Parrot.Events;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class ExitReminderTurnCompletionCallback(
    IExitReminder exitReminder,
    IAgentHistory history,
    IEventBroker eventBroker) : IAgentTurnCompletionCallback
{
    private const string NoticeKind = "exit_reminder";

    public async ValueTask<AgentTurnCompletionOutcome> Complete(
        AgentTurnCompletionCandidate candidate,
        CancellationToken cancellationToken)
    {
        var reminder = exitReminder.Build();
        if (reminder is null)
        {
            return AgentTurnCompletionOutcome.Continue();
        }

        var published = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = candidate.SessionId,
            AgentNotice = new AgentNotice { Kind = NoticeKind, Notice = "Exit reminder injected" },
        };
        history.AppendRetainedReminder(published, candidate.AssistantText, reminder);
        await eventBroker.PublishWithCancellation(published, cancellationToken).ConfigureAwait(false);
        return AgentTurnCompletionOutcome.Retry(reminder, true, true);
    }
}
