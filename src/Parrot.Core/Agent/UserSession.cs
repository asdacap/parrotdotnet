using Parrot.Config;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Permissions;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.Questions;
using Parrot.Queues;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Agent;

// One working directory's session. It owns the event stream and every
// agent session inside it, which is why the stream is here and not on an
// agent session: a subagent runs in a background child session, and its events
// are republished on this one stream so a client needs a single subscription
// however deep the recursion goes.
internal sealed class UserSession : IAsyncDisposable
{
    private readonly List<IAgentSessionScope> _agents = [];
    private readonly EventBroker _eventBroker = new();
    private readonly EventRepository _eventRepository;
    private readonly IAgentSessionFactory _agentSessions;
    private readonly SessionResourceLease _resources;
    private readonly Lock _mainGate = new();
    private readonly UserSessionModes _modes;
    private readonly PromptTemplateCatalog _promptTemplates;

    // What every drain inside this session is bounded by. It is owned here
    // rather than by an agent session because the drain outlives the request
    // that woke it, and this is the thing whose lifetime it should match.
    private readonly CancellationTokenSource _lifetime = new();

    // The main agent session is built after the rest of this owner has been
    // initialized so recovered durable work can resume immediately. Its id is
    // settled first so History can address it throughout construction.
    private readonly string _mainSessionId;
    private readonly string _rootAgentName;
    private ModelSelector _model;
    private IAgentSession? _main;

    public UserSession(
        string id,
        string rootAgentName,
        ResolvedModelSelection model,
        string mode,
        SessionResourceLease resources,
        IAgentSessionFactorySource agentSessionFactories,
        UserSessionModes modes,
        PromptTemplateCatalog promptTemplates,
        ProfileRegistry profiles,
        bool interactivePermissions,
        TimeSpan userInputTimeout,
        TimeProvider timeProvider)
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
        _promptTemplates = promptTemplates ?? throw new ArgumentNullException(nameof(promptTemplates));
        var state = _eventRepository.SessionState(id, modes.Resolve(mode).Id);
        _mainSessionId = state.AgentSessionId;
        _modes.Attach(resources.Resources.AgentScratch(_mainSessionId));
        Mode = modes.Resolve(state.Mode);
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        Questions = new QuestionBroker(userInputTimeout, TimeProvider);
        Permissions = new PermissionBroker(
            _eventBroker,
            _eventRepository,
            interactivePermissions,
            userInputTimeout,
            TimeProvider);
        QueueCatalog = agentSessionFactories.CreateQueueCatalog(this);
        ShellProcesses = agentSessionFactories.CreateShellProcesses(this);
        _agentSessions = agentSessionFactories.Create(this);
        var retainedAgents = new RetainedAgentBudget(1024);
        Registry = new AgentRegistry(_agentSessions, _eventBroker, _eventRepository, profiles, _promptTemplates, retainedAgents, _lifetime.Token);
        Status = new RuntimeStatus(QueueCatalog, ShellProcesses, Registry, _promptTemplates, TimeProvider);
        Registry.AttachStatus(Status);
        foreach (var agentSessionId in _eventRepository.AgentHistorySessionIds())
        {
            _ = _eventRepository.PrepareAgentHistory(agentSessionId);
        }

        var main = InitializeMain();
        main.UseResolvedSelection(model);
        main.Recover();
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
    public IMode Mode { get; private set; }

    internal CancellationToken Lifetime => _lifetime.Token;

    internal TimeProvider TimeProvider { get; }

    internal UserSessionResources Resources => _resources.Resources;

    internal ImageArtifactRepository Images => _resources.Images;

    internal AgentQueueCatalog QueueCatalog { get; }

    internal ShellProcessOwners ShellProcesses { get; }

    internal AgentRegistry Registry { get; }

    internal RuntimeStatus Status { get; }

    internal QuestionBroker Questions { get; }

    internal PermissionBroker Permissions { get; }

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

    public IMode ResolveMode(string mode) => _modes.Resolve(mode);

    public void UpdateSelection(ResolvedModelSelection model) => Update(model, null);

    public void Update(ResolvedModelSelection? model, IMode? profile)
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
            if (model is not null)
            {
                _main?.UseResolvedSelection(model);
            }
        }
    }

    // Indefinite by design. It ends when the caller stops listening, not when
    // a turn finishes -- and it does not wait for an agent session to exist, so
    // a client can subscribe before sending anything.
    public async IAsyncEnumerable<Event> Listen(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var events = _eventBroker.Subscribe();
        _ = Main();
        using var queues = QueueCatalog.SubscribeInventory();
        using var processes = ShellProcesses.SubscribeInventory();
        var initialUsage = _eventRepository.Usage();

        var initialQueues = await queues.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var published in QueueInventoryProtocol.Convert(initialQueues, _mainSessionId))
        {
            yield return published;
        }

        var initialProcesses = await processes.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var published in ShellProcessInventoryProtocol.Convert(initialProcesses))
        {
            yield return published;
        }

        yield return initialUsage.ConvertToEvent();

        var eventPending = (Task<bool>?)events.Reader.WaitToReadAsync(cancellationToken).AsTask();
        var queuePending = (Task<bool>?)queues.Reader.WaitToReadAsync(cancellationToken).AsTask();
        var processPending = (Task<bool>?)processes.Reader.WaitToReadAsync(cancellationToken).AsTask();

        while (eventPending is not null || queuePending is not null || processPending is not null)
        {
            var pending = new[] { eventPending, queuePending, processPending }
                .Where(task => task is not null)
                .Select(task => task ?? throw new InvalidOperationException("inventory read is missing"));
            var completed = await Task.WhenAny(pending).ConfigureAwait(false);

            if (ReferenceEquals(completed, eventPending))
            {
                if (!await completed.ConfigureAwait(false))
                {
                    eventPending = null;
                    continue;
                }

                if (events.Reader.TryRead(out var published))
                {
                    yield return published;
                }

                eventPending = events.Reader.WaitToReadAsync(cancellationToken).AsTask();
                continue;
            }

            if (ReferenceEquals(completed, queuePending))
            {
                if (!await completed.ConfigureAwait(false))
                {
                    queuePending = null;
                    continue;
                }

                if (queues.Reader.TryRead(out var snapshot))
                {
                    foreach (var published in QueueInventoryProtocol.Convert(snapshot, _mainSessionId))
                    {
                        yield return published;
                    }
                }

                queuePending = queues.Reader.WaitToReadAsync(cancellationToken).AsTask();
                continue;
            }

            if (!await completed.ConfigureAwait(false))
            {
                processPending = null;
                continue;
            }

            if (processes.Reader.TryRead(out var processSnapshot))
            {
                foreach (var published in ShellProcessInventoryProtocol.Convert(processSnapshot))
                {
                    yield return published;
                }
            }

            processPending = processes.Reader.WaitToReadAsync(cancellationToken).AsTask();
        }
    }

    // The user talks to the user session; the main agent session is what
    // actually runs the turn. Admitting is not running it: it returns as soon
    // as the prompt is durable, whether or not a turn was already in flight.
    public async Task<Admission> Send(
        string prompt, string messageId, Delivery delivery, CancellationToken cancellationToken) =>
        await Send([ConversationPart.TextPart(prompt)], messageId, delivery, cancellationToken).ConfigureAwait(false);

    public async Task<Admission> Send(
        IReadOnlyList<ConversationPart> parts,
        string messageId,
        Delivery delivery,
        CancellationToken cancellationToken)
    {
        var (admission, _) = await Main().Send(parts, messageId, delivery, cancellationToken)
            .ConfigureAwait(false);
        return admission;
    }

    // Stops the main turn in flight. Parent-owned children outlive the tool
    // call that spawned them and are stopped recursively at user-session shutdown.
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
        Permissions.Dispose();
        await Registry.DisposeAsync().ConfigureAwait(false);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await ShellProcesses.Settle().ConfigureAwait(false);

        foreach (var agent in _agents)
        {
            Registry.UnregisterRootScope(agent);
            await agent.DisposeAsync().ConfigureAwait(false);
        }

        // Ends every subscription on this session's stream. A listener blocked
        // on MoveNext returns false rather than waiting forever.
        _lifetime.Dispose();
        _eventBroker.Dispose();
        ShellProcesses.Dispose();
        QueueCatalog.Dispose();
        _agents.Clear();
        await _resources.DisposeAsync().ConfigureAwait(false);
    }

    internal IReadOnlyList<ActiveWorkObservation> ActiveWork() => [.. ShellProcesses.Active(), .. Registry.Active()];

    internal Task SetGoal(string goal, CancellationToken cancellationToken) =>
        Main().SetGoal(goal, cancellationToken);

    internal void ClearGoal() => Main().ClearGoal();

    internal Task Compact(CancellationToken cancellationToken) =>
        Main().Compact(cancellationToken);

    // Built once after owner initialization. The lock also protects concurrent
    // access from RPC handlers throughout the session lifetime.
    private IAgentSession Main()
    {
        lock (_mainGate)
        {
            return _main ?? throw new InvalidOperationException("the main agent session is not initialized");
        }
    }

    private IAgentSession InitializeMain()
    {
        var scope = _agentSessions.Create(
            AgentIdentity.Main(_mainSessionId, _rootAgentName, _promptTemplates),
            AgentSessionParentScope.Root(),
            _model,
            _eventBroker,
            _eventRepository,
            Mode,
            Mode.SecurityProfile,
            Status,
            Registry,
            _lifetime.Token);
        try
        {
            Registry.RegisterRootScope(scope);
            lock (_mainGate)
            {
                _main = scope.Session;
                _agents.Add(scope);
                return _main;
            }
        }
        catch
        {
            _agents.Add(scope);
            throw;
        }
    }
}
