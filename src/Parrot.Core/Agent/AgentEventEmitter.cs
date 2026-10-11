using Parrot.Diagnostics;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

// Commits the event and its projection, then publishes it. In that order:
// EventBroker must only ever hand a subscriber an event the repository has
// already committed, so nothing observable can be un-happened by a crash.
internal sealed class AgentEventEmitter(
    AgentIdentity identity,
    IAgentHistory history,
    IEventBroker eventBroker,
    IDiagnosticLog diagnostics)
{
    public ValueTask Emit(
        Event published,
        string? role,
        string? content,
        CancellationToken cancellationToken) =>
        Commit(published, () => history.Append(published, role, content), cancellationToken);

    public ValueTask EmitMessage(
        Event published,
        LLMMessage message,
        ConversationOrigin origin,
        CancellationToken cancellationToken) =>
        Commit(published, () => history.AppendMessage(published, message, origin), cancellationToken);

    private async ValueTask Commit(Event published, Func<SessionUsage?> append, CancellationToken cancellationToken)
    {
        try
        {
            _ = append();
        }
        catch (Exception failure)
        {
            diagnostics.Write(new("persistence", "append_failed", DiagnosticSeverity.Error)
            {
                AgentSessionId = identity.SessionId,
                CorrelationId = published.Id,
                ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
            });
            throw;
        }

        await eventBroker.PublishWithCancellation(published, cancellationToken).ConfigureAwait(false);
    }
}
