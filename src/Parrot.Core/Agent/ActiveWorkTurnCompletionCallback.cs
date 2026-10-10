using Parrot.Events;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class ActiveWorkTurnCompletionCallback(
    ActiveWorkCompletionReminder activeWorkReminder,
    IAgentHistory history,
    IEventBroker eventBroker) : IAgentTurnCompletionCallback
{
    private const string NoticeKind = "active_work_reminder";

    public async ValueTask<AgentTurnCompletionOutcome> Complete(
        AgentTurnCompletionCandidate candidate,
        CancellationToken cancellationToken)
    {
        var reminder = activeWorkReminder.Build();
        if (reminder is null)
        {
            return AgentTurnCompletionOutcome.Continue();
        }

        var published = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = candidate.SessionId,
            AgentNotice = new AgentNotice { Kind = NoticeKind, Notice = "Active work reminder injected" },
        };
        history.AppendSystemPrompt(published, reminder);
        await eventBroker.PublishWithCancellation(published, cancellationToken).ConfigureAwait(false);
        return AgentTurnCompletionOutcome.Retry(reminder, false, null);
    }
}
