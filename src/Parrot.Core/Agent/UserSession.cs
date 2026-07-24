using System.Collections.Concurrent;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

// One working directory's session. It owns the event stream and every
// AgentSession inside it, which is why the stream is here and not on an
// AgentSession: a subagent runs in a background child session, and its events
// are republished on this one stream so a client needs a single subscription
// however deep the recursion goes.
//
// M1 holds no database and no working-directory claim yet; that is M2.
internal sealed class UserSession : IDisposable
{
    private readonly ConcurrentDictionary<string, AgentSession> _agents = new(StringComparer.Ordinal);
    private readonly EventBroker _eventBroker = new();
    private readonly AgentSession _main;

    private readonly EventRepository _eventRepository;

    public UserSession(string id, string model, ILLMProvider provider, EventRepository eventRepository)
    {
        Id = id;
        _eventRepository = eventRepository;
        _main = new AgentSession(Identifier.AgentSession(), provider, _eventBroker, eventRepository) { Model = model };
        _ = _agents.TryAdd(_main.SessionId, _main);
    }

    public string Id { get; }

    public string Model
    {
        get => _main.Model;
        set => _main.Model = value;
    }

    // Indefinite by design. It ends when the caller stops listening, not when
    // a turn finishes.
    public IAsyncEnumerable<Event> Listen(CancellationToken cancellationToken) =>
        _eventBroker.Subscribe(cancellationToken);

    // The user talks to the user session; the main agent session is what
    // actually runs the turn. Subagents join _agents later, from the agent side.
    public void Send(string prompt, CancellationToken cancellationToken) =>
        _main.Start(prompt, cancellationToken);

    // What a resumed session already said. Read from the projection, never by
    // replaying the raw event log.
    public IReadOnlyList<string> History() => _eventRepository.Messages(_main.SessionId);

    // Ends every subscription on this session's stream. A listener blocked on
    // MoveNext returns false rather than waiting forever.
    public void Dispose()
    {
        _eventBroker.Dispose();
        _agents.Clear();
    }
}
