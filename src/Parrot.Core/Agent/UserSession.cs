using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Agent;

// One working directory's session. It owns the event stream and every
// AgentSession inside it, which is why the stream is here and not on an
// AgentSession: a subagent runs in a background child session, and its events
// are republished on this one stream so a client needs a single subscription
// however deep the recursion goes.
internal sealed class UserSession : IAsyncDisposable
{
    private readonly List<AgentSession> _agents = [];
    private readonly EventBroker _eventBroker = new();
    private readonly EventRepository _eventRepository;
    private readonly IAgentSessionFactory _agentSessions;
    private readonly Lock _mainGate = new();
    private readonly ModeRegistry _modes;
    private readonly RuntimeStatus _status;

    // What every drain inside this session is bounded by. It is owned here
    // rather than by an agent session because the drain outlives the request
    // that woke it, and this is the thing whose lifetime it should match.
    private readonly CancellationTokenSource _lifetime = new();

    // The main agent session is not built here. Its tools are constructed with
    // the session they belong to, and their factories with this user session,
    // so building it in the constructor would need a `this` that does not
    // finish existing until the constructor returns. It is built on the first
    // prompt instead; its id is settled now so History has something to ask
    // about before then.
    private readonly string _mainSessionId;
    private ILLMProvider _provider;
    private AgentSession? _main;

    public UserSession(
        string id,
        ILLMProvider provider,
        string providerId,
        string model,
        string mode,
        EventRepository eventRepository,
        IAgentSessionFactorySource agentSessionFactories,
        ModeRegistry modes)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(agentSessionFactories);

        Id = id;
        ProviderId = providerId;
        Model = model;
        _eventRepository = eventRepository;
        _modes = modes;
        var state = eventRepository.SessionState(id, modes.Resolve(mode, id).Id);
        _mainSessionId = state.AgentSessionId;
        Mode = modes.Resolve(state.Mode, id);
        _provider = provider;
        ShellProcesses = agentSessionFactories.CreateShellProcesses(this);
        _agentSessions = agentSessionFactories.Create(this, provider);
        Registry = new AgentRegistry(_agentSessions, _eventBroker, _eventRepository, _lifetime.Token);
        _status = new RuntimeStatus(this);
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

    public ModeProfile Mode { get; private set; }

    internal CancellationToken Lifetime => _lifetime.Token;

    internal ShellProcessOwner ShellProcesses { get; }

    internal AgentRegistry Registry { get; }

    // Assigned, never rebuilt. The main session holds the conversation, the
    // input admitted against it and the drain that may be running: replacing it
    // to change a model would throw all three away, and a turn in flight would
    // carry on inside a session nothing points at any more.
    public void UpdateMode(string mode)
    {
        var selected = _modes.Resolve(mode, Id);

        lock (_mainGate)
        {
            if (string.Equals(Mode.Id, selected.Id, StringComparison.Ordinal))
            {
                return;
            }

            _eventRepository.UpdateMode(Id, _mainSessionId, selected.Id);
            Mode = selected;

            _main?.UpdateSelection(_provider, Model, selected);
        }
    }

    public void UpdateSelection(ILLMProvider provider, string providerId, string model) =>
        Update(provider, providerId, model, null);

    public void Update(
        ILLMProvider? provider,
        string? providerId,
        string? model,
        ModeProfile? mode)
    {
        lock (_mainGate)
        {
            if (provider is not null && providerId is not { Length: > 0 })
            {
                throw new ArgumentException("A provider ID is required with a provider.", nameof(providerId));
            }

            if (provider is not null && model is not { Length: > 0 })
            {
                throw new ArgumentException("A model is required with a provider.", nameof(model));
            }

            if (mode is not null && !string.Equals(Mode.Id, mode.Id, StringComparison.Ordinal))
            {
                _eventRepository.UpdateMode(Id, _mainSessionId, mode.Id);
                Mode = mode;
            }

            if (provider is not null && providerId is { Length: > 0 } selectedProviderId &&
                model is { Length: > 0 } selectedModel)
            {
                _provider = provider;
                ProviderId = selectedProviderId;
                Model = selectedModel;
            }

            _main?.UpdateSelection(_provider, Model, Mode);
        }
    }

    // Indefinite by design. It ends when the caller stops listening, not when
    // a turn finishes -- and it does not wait for an agent session to exist, so
    // a client can subscribe before sending anything.
    public IAsyncEnumerable<Event> Listen(CancellationToken cancellationToken) =>
        _eventBroker.Subscribe(cancellationToken);

    // The user talks to the user session; the main agent session is what
    // actually runs the turn. Admitting is not running it: it returns as soon
    // as the prompt is durable, whether or not a turn was already in flight.
    public Task<Admission> Send(
        string prompt, string messageId, Delivery delivery, CancellationToken cancellationToken) =>
        Main().Send(prompt, messageId, delivery, cancellationToken);

    // Stops the main turn in flight. Registry-owned children outlive the tool
    // call that spawned them and are stopped separately at user-session shutdown.
    public Task Interrupt(CancellationToken cancellationToken) =>
        Main().Interrupt(cancellationToken);

    // What a resumed session already said. Read from the projection, never by
    // replaying the raw event log.
    public IReadOnlyList<string> History() => _eventRepository.Messages(_mainSessionId);

    // Asynchronous because ending this session means waiting for its drains,
    // and only then closing what they write to. A synchronous Dispose would
    // close the database under a drain still unwinding into it, which is a
    // crash rather than a shutdown -- and a separate Stop the caller has to
    // remember would be the same crash whenever anyone forgot.
    public async ValueTask DisposeAsync()
    {
        await Registry.DisposeAsync().ConfigureAwait(false);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await ShellProcesses.Settle().ConfigureAwait(false);

        foreach (var agent in _agents)
        {
            await agent.Settled().ConfigureAwait(false);
        }

        // Ends every subscription on this session's stream. A listener blocked
        // on MoveNext returns false rather than waiting forever.
        _lifetime.Dispose();
        _eventBroker.Dispose();
        _agents.Clear();
    }

    internal IReadOnlyList<ActiveWorkObservation> ActiveWork() => [.. ShellProcesses.Active(), .. Registry.Active()];

    // Built once, on the first prompt. Under a lock because SendMessage arrives
    // on gRPC handler threads and two concurrent first prompts would otherwise
    // each build a main session.
    private AgentSession Main()
    {
        lock (_mainGate)
        {
            if (_main is null)
            {
                _main = _agentSessions.Create(
                    AgentIdentity.Main(_mainSessionId),
                    _provider,
                    Model,
                    _eventBroker,
                    _eventRepository,
                    Mode,
                    _status,
                    _lifetime.Token);
                _agents.Add(_main);
            }

            return _main;
        }
    }
}
