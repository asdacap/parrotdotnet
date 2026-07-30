using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.Questions;
using Parrot.Queues;
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
    private readonly List<IAgentSessionLease> _agents = [];
    private readonly EventBroker _eventBroker = new();
    private readonly EventRepository _eventRepository;
    private readonly IAgentSessionFactory _agentSessions;
    private readonly SessionResourceLease _resources;
    private readonly Lock _mainGate = new();
    private readonly UserSessionModes _modes;
    private readonly SemaphoreSlim _queueDelivery = new(1, 1);

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
    private readonly string _rootAgentName;
    private ModelSelector _model;
    private AgentSession? _main;

    public UserSession(
        string id,
        string rootAgentName,
        ResolvedModelSelection model,
        string mode,
        SessionResourceLease resources,
        IAgentSessionFactorySource agentSessionFactories,
        UserSessionModes modes)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(agentSessionFactories);

        Id = id;
        _rootAgentName = rootAgentName;
        _model = model.RequestedSelector;
        ProviderId = model.CanonicalModel.Provider.Id;
        CanonicalModel = model.CanonicalModel.Selector;
        _resources = resources;
        _eventRepository = resources.Events;
        _modes = modes;
        var state = _eventRepository.SessionState(id, modes.Resolve(mode).Id);
        _mainSessionId = state.AgentSessionId;
        Mode = modes.Resolve(state.Mode);
        Queues = agentSessionFactories.CreateQueues(this);
        ShellProcesses = agentSessionFactories.CreateShellProcesses(this);
        _agentSessions = agentSessionFactories.Create(this);
        Registry = new AgentRegistry(_agentSessions, _eventBroker, _eventRepository, modes.Profiles, _lifetime.Token);
        Status = new RuntimeStatus(this);
        Registry.AttachStatus(Status);
    }

    public string Id { get; }

    public string ProviderId { get; private set; }

    public string CanonicalModel { get; private set; }

    // Session state, and owned here rather than on the main agent session:
    // CreateSession reports it and UpdateSession changes it, both of which can
    // happen before the first prompt builds an agent session at all.
    // Under the same lock as Main: an UpdateSession racing the first prompt
    // would otherwise be free to see a null _main, skip, and lose the selection
    // the turn is about to run with.
    public string Model => _model.Value;

    // The user-selected foreground mode. The resolved profile is applied only
    // to this user session's main agent; child agents select their own profile.
    public MainAgentProfile Mode { get; private set; }

    internal CancellationToken Lifetime => _lifetime.Token;

    internal UserSessionResources Resources => _resources.Resources;

    internal QueueStore Queues { get; }

    internal ShellProcessOwners ShellProcesses { get; }

    internal AgentRegistry Registry { get; }

    internal RuntimeStatus Status { get; }

    internal QuestionBroker Questions { get; } = new();

    // Assigned, never rebuilt. The main session holds the conversation, the
    // input admitted against it and the drain that may be running: replacing it
    // to change a model would throw all three away, and a turn in flight would
    // carry on inside a session nothing points at any more.
    public void UpdateMode(string mode)
    {
        var selected = _modes.Resolve(mode);

        lock (_mainGate)
        {
            if (string.Equals(Mode.Id, selected.Id, StringComparison.Ordinal))
            {
                return;
            }

            _eventRepository.UpdateMode(Id, _mainSessionId, selected.Id);
            Mode = selected;

            _main?.UpdateSelection(_model, selected);
        }
    }

    public MainAgentProfile ResolveMode(string mode) => _modes.Resolve(mode);

    public void UpdateSelection(ResolvedModelSelection model) => Update(model, null);

    public void Update(ResolvedModelSelection? model, MainAgentProfile? profile)
    {
        lock (_mainGate)
        {
            if (profile is not null && !string.Equals(Mode.Id, profile.Id, StringComparison.Ordinal))
            {
                _eventRepository.UpdateMode(Id, _mainSessionId, profile.Id);
                Mode = profile;
            }

            if (model is not null)
            {
                _model = model.RequestedSelector;
                ProviderId = model.CanonicalModel.Provider.Id;
                CanonicalModel = model.CanonicalModel.Selector;
            }

            _main?.UpdateSelection(_model, Mode);
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
    public async Task<Admission> Send(
        string prompt, string messageId, Delivery delivery, CancellationToken cancellationToken)
    {
        var (admission, _) = await Main().Send(prompt, messageId, delivery, cancellationToken)
            .ConfigureAwait(false);
        return admission;
    }

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
        Questions.Dispose();
        await Registry.DisposeAsync().ConfigureAwait(false);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await ShellProcesses.Settle().ConfigureAwait(false);

        foreach (var agent in _agents)
        {
            await agent.Session.Settled().ConfigureAwait(false);
            await agent.DisposeAsync().ConfigureAwait(false);
        }

        // Ends every subscription on this session's stream. A listener blocked
        // on MoveNext returns false rather than waiting forever.
        _lifetime.Dispose();
        _eventBroker.Dispose();
        Queues.Dispose();
        _queueDelivery.Dispose();
        _agents.Clear();
        await _resources.DisposeAsync().ConfigureAwait(false);
    }

    internal IReadOnlyList<ActiveWorkObservation> ActiveWork() => [.. ShellProcesses.Active(), .. Registry.Active()];

    internal async Task<bool> DeliverMonitored(AgentSession root, CancellationToken cancellationToken)
    {
        if (root.Depth != 0 || !root.IsIdle())
        {
            return false;
        }

        await _queueDelivery.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!root.IsIdle())
            {
                return false;
            }

            return await Queues.DeliverMonitored(root.ReceiveQueueNotification, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            _ = _queueDelivery.Release();
        }
    }

    internal async Task NotifyQueuePush(CancellationToken cancellationToken)
    {
        try
        {
            _ = await DeliverMonitored(Main(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
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
                var lease = _agentSessions.Create(
                    AgentIdentity.Main(_mainSessionId, _rootAgentName),
                    _model,
                    _eventBroker,
                    _eventRepository,
                    Mode,
                    Mode.SecurityProfile,
                    Status,
                    Registry,
                    _lifetime.Token);
                _main = lease.Session;
                _agents.Add(lease);
            }

            return _main;
        }
    }
}
