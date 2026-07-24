using Parrot.Agent;
using Parrot.Events;

namespace Parrot.Protocol;

// One session's runtime: its drain and the stream it publishes to. Held by
// ParrotService so SendMessage and Listen reach the same one by id.
internal sealed class SessionHost(AgentSession session, EventBroker events)
{
    public AgentSession Session { get; } = session;

    public EventBroker Events { get; } = events;

    public Task Turn { get; set; } = Task.CompletedTask;

    public string ParentSessionId { get; set; } = string.Empty;
}
