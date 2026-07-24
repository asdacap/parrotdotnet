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
internal sealed class UserSession : IDisposable
{
    private readonly ConcurrentDictionary<string, AgentSession> _agents = new(StringComparer.Ordinal);
    private readonly EventBroker _eventBroker = new();
    private readonly EventRepository _eventRepository;
    private readonly IAgentSessionFactory _agentSessions;
    private readonly Lock _mainGate = new();

    // The main agent session is not built here. Its tools are constructed with
    // the session they belong to, and their factories with this user session,
    // so building it in the constructor would need a `this` that does not
    // finish existing until the constructor returns. It is built on the first
    // prompt instead; its id is settled now so History has something to ask
    // about before then.
    private readonly string _mainSessionId = Identifier.AgentSession();
    private ILLMProvider _provider;
    private AgentSession? _main;

    public UserSession(
        string id,
        ILLMProvider provider,
        string providerId,
        string model,
        EventRepository eventRepository,
        IAgentSessionFactorySource agentSessionFactories)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(agentSessionFactories);

        Id = id;
        ProviderId = providerId;
        Model = model;
        _eventRepository = eventRepository;
        _provider = provider;
        _agentSessions = agentSessionFactories.Create(this, provider);
    }

    public string Id { get; }

    public string ProviderId { get; private set; }

    // Session state, and owned here rather than on the main agent session:
    // CreateSession reports it and UpdateSession changes it, both of which can
    // happen before the first prompt builds an agent session at all.
    // Under the same lock as Main: an UpdateSession racing the first prompt
    // would otherwise be free to see a null _main, skip, and lose the selection
    // the turn is about to run with.
    public string Model { get; private set; }

    public void UpdateSelection(ILLMProvider provider, string providerId, string model)
    {
        ArgumentNullException.ThrowIfNull(provider);

        lock (_mainGate)
        {
            _provider = provider;
            ProviderId = providerId;
            Model = model;

            if (_main is not null)
            {
                _main = _agentSessions.Create(_mainSessionId, _provider, Model, 0, _eventBroker, _eventRepository);
                _agents[_mainSessionId] = _main;
            }
        }
    }

    // Indefinite by design. It ends when the caller stops listening, not when
    // a turn finishes -- and it does not wait for an agent session to exist, so
    // a client can subscribe before sending anything.
    public IAsyncEnumerable<Event> Listen(CancellationToken cancellationToken) =>
        _eventBroker.Subscribe(cancellationToken);

    // The user talks to the user session; the main agent session is what
    // actually runs the turn.
    public void Send(string prompt, CancellationToken cancellationToken) =>
        Main().Start(prompt, cancellationToken);

    // A subagent joining this session, registered by the tool that spawned it
    // so it is visible while it runs rather than only once it finishes.
    public void Admit(AgentSession agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        _ = _agents.TryAdd(agent.SessionId, agent);
    }

    // What a resumed session already said. Read from the projection, never by
    // replaying the raw event log.
    public IReadOnlyList<string> History() => _eventRepository.Messages(_mainSessionId);

    // Ends every subscription on this session's stream. A listener blocked on
    // MoveNext returns false rather than waiting forever.
    public void Dispose()
    {
        _eventBroker.Dispose();
        _agents.Clear();
    }

    // Built once, on the first prompt. Under a lock because SendMessage arrives
    // on gRPC handler threads and two concurrent first prompts would otherwise
    // each build a main session.
    private AgentSession Main()
    {
        lock (_mainGate)
        {
            if (_main is null)
            {
                _main = _agentSessions.Create(_mainSessionId, _provider, Model, 0, _eventBroker, _eventRepository);
                Admit(_main);
            }

            return _main;
        }
    }
}
