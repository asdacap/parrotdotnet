using Parrot.Config;
using Parrot.Context;
using Parrot.Diagnostics;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Permissions;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.Questions;
using Parrot.Queues;
using Parrot.Skills;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Agent;

// One working directory's session. It owns the event stream and every
// agent session inside it, which is why the stream is here and not on an
// agent session: a subagent runs in a background child session, and its events
// are republished on this one stream so a client needs a single subscription
// however deep the recursion goes.
internal sealed class UserSession : IUserSession
{
    private readonly List<IAgentSessionScope> _agents = [];
    private readonly IEventBroker _eventBroker;
    private readonly IEventRepository _eventRepository;
    private readonly IAgentSessionFactory _agentSessions;
    private readonly ISessionResourceLease _resources;
    private readonly Lock _mainGate = new();
    private readonly Lock _disposalGate = new();
    private readonly UserSessionModes _modes;
    private readonly IPromptTemplateCatalog _promptTemplates;

    // What every drain inside this session is bounded by. It is owned here
    // rather than by an agent session because the drain outlives the request
    // that woke it, and this is the thing whose lifetime it should match.
    private readonly CancellationTokenSource _lifetime;

    // The main agent session is built after the rest of this owner has been
    // initialized so recovered durable work can resume immediately. Its id is
    // settled first so History can address it throughout construction.
    private readonly string _mainSessionId;
    private readonly string _rootAgentName;
    private ModelSelector _model;
    private IAgentSessionScope? _main;
    private Task? _disposal;

    private UserSession(
        string id,
        string rootAgentName,
        ResolvedModelSelection model,
        string mode,
        ISessionResourceLease resources,
        IAgentSessionFactorySource agentSessionFactories,
        UserSessionModes modes,
        IPromptTemplateCatalog promptTemplates,
        ProfileRegistry profiles,
        SkillCatalogFactory skillCatalogFactory,
        bool interactivePermissions,
        TimeSpan userInputTimeout,
        TimeProvider timeProvider,
        Func<IEventBroker> createEventBroker,
        Stack<Func<ValueTask>> cleanup,
        CancellationTokenSource lifetime)
    {
        _resources = resources;
        _eventBroker = createEventBroker();
        _lifetime = lifetime;
        cleanup.Push(() =>
        {
            _lifetime.Dispose();
            return ValueTask.CompletedTask;
        });
        cleanup.Push(() =>
        {
            _eventBroker.Dispose();
            return ValueTask.CompletedTask;
        });
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(agentSessionFactories);

        Id = id;
        _rootAgentName = rootAgentName;
        _model = model.RequestedSelector;
        ProviderId = model.CanonicalModel.Provider.Id;
        CanonicalModel = model.CanonicalModel.Selector;
        _eventRepository = resources.Events;
        _modes = modes;
        _promptTemplates = promptTemplates ?? throw new ArgumentNullException(nameof(promptTemplates));
        SkillCatalog = skillCatalogFactory.Create(resources.Resources.Workspace);
        var state = _eventRepository.SessionState(id, modes.Resolve(mode).Profile.Id);
        _mainSessionId = state.AgentSessionId;
        _ = _eventRepository.GetRuntimeStatistics();
        _modes.Attach(resources.Resources.AgentScratch(_mainSessionId));
        Mode = modes.Resolve(state.Mode);
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        Questions = new QuestionBroker(userInputTimeout, TimeProvider, Diagnostics);
        cleanup.Push(() =>
        {
            Questions.Dispose();
            return ValueTask.CompletedTask;
        });
        Permissions = new PermissionBroker(
            _eventBroker,
            _eventRepository,
            interactivePermissions,
            userInputTimeout,
            TimeProvider,
            Diagnostics);
        cleanup.Push(() =>
        {
            Permissions.Dispose();
            return ValueTask.CompletedTask;
        });
        if (Directory.Exists(Resources.AgentQueueRootDirectory))
        {
            Directory.Delete(Resources.AgentQueueRootDirectory, recursive: true);
        }

        _agentSessions = agentSessionFactories.Create(this);
        var retainedAgents = new RetainedAgentBudget(1024);
        Registry = new AgentRegistry(_agentSessions, _eventBroker, _eventRepository, profiles, _promptTemplates, retainedAgents, Diagnostics, _lifetime.Token);
        cleanup.Push(Registry.BeginShutdown);
        Status = new RuntimeStatus(
            Registry,
            _promptTemplates,
            TimeProvider);
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
    public IMode Mode { get; private set; }

    public CancellationToken Lifetime => _lifetime.Token;

    public TimeProvider TimeProvider { get; }

    public UserSessionResources Resources => _resources.Resources;

    public IDiagnosticLog Diagnostics => _resources.Diagnostics;

    public IImageArtifactRepository Images => _resources.Images;

    public IAgentRegistry Registry { get; }

    public IRuntimeStatus Status { get; }

    public IQuestionBroker Questions { get; }

    public IPermissionBroker Permissions { get; }

    public ISkillCatalog SkillCatalog { get; }

    public static async Task<IUserSession> Create(
        string id,
        string rootAgentName,
        ResolvedModelSelection model,
        string mode,
        ISessionResourceLease resources,
        IAgentSessionFactorySource agentSessionFactories,
        UserSessionModes modes,
        IPromptTemplateCatalog promptTemplates,
        ProfileRegistry profiles,
        SkillCatalogFactory skillCatalogFactory,
        bool interactivePermissions,
        TimeSpan userInputTimeout,
        TimeProvider timeProvider,
        Func<IEventBroker> createEventBroker)
    {
        var cleanup = new Stack<Func<ValueTask>>();
        var lifetime = new CancellationTokenSource();
        UserSession? session = null;
        try
        {
            session = new UserSession(
                id,
                rootAgentName,
                model,
                mode,
                resources,
                agentSessionFactories,
                modes,
                promptTemplates,
                profiles,
                skillCatalogFactory,
                interactivePermissions,
                userInputTimeout,
                timeProvider,
                createEventBroker,
                cleanup,
                lifetime);
            session.Diagnostics.Write(new("session", "recovering", DiagnosticSeverity.Information));
            foreach (var agentSessionId in session._eventRepository.AgentHistorySessionIds())
            {
                new AgentHistoryFile(session.Resources, agentSessionId).Refresh(session._eventRepository, agentSessionId);
            }

            var main = session.InitializeMain();
            main.UseResolvedSelection(model);
            main.Recover();
            session.Diagnostics.Write(new("session", "recovered", DiagnosticSeverity.Information));
            return session;
        }
        catch (Exception failure)
        {
            resources.Diagnostics.Write(new("session", "initialization_failed", DiagnosticSeverity.Error)
            {
                ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
            });
            if (session is not null)
            {
                cleanup.Clear();
                cleanup.Push(session.DisposeAsync);
            }

            try
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception cancellationFailure)
            {
                resources.Diagnostics.Write(new("session", "cleanup_failed", DiagnosticSeverity.Error)
                {
                    ErrorCode = DiagnosticEvent.ClassifyFailure(cancellationFailure),
                });
            }

            while (cleanup.TryPop(out var dispose))
            {
                try
                {
                    await dispose().ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    resources.Diagnostics.Write(new("session", "cleanup_failed", DiagnosticSeverity.Error)
                    {
                        ErrorCode = DiagnosticEvent.ClassifyFailure(cleanupFailure),
                    });
                }
            }

            throw;
        }
    }

    // Assigned, never rebuilt. The main session holds the conversation, the
    // input admitted against it and the drain that may be running: replacing it
    // to change a model would throw all three away, and a turn in flight would
    // carry on inside a session nothing points at any more.
    public void UpdateMode(string mode)
    {
        var selected = _modes.Resolve(mode);

        lock (_mainGate)
        {
            if (string.Equals(Mode.Profile.Id, selected.Profile.Id, StringComparison.Ordinal))
            {
                return;
            }

            _eventRepository.UpdateMode(Id, _mainSessionId, selected.Profile.Id);
            Mode = selected;

            _main?.Session.UpdateSelection(_model, selected);
        }

        Diagnostics.Write(new("session", "mode_changed", DiagnosticSeverity.Information));
    }

    public IMode ResolveMode(string mode) => _modes.Resolve(mode);

    public void UpdateSelection(ResolvedModelSelection model) => Update(model, null);

    public void Update(ResolvedModelSelection? model, IMode? mode)
    {
        lock (_mainGate)
        {
            if (mode is not null && !string.Equals(Mode.Profile.Id, mode.Profile.Id, StringComparison.Ordinal))
            {
                _eventRepository.UpdateMode(Id, _mainSessionId, mode.Profile.Id);
                Mode = mode;
            }

            if (model is not null)
            {
                _model = model.RequestedSelector;
                ProviderId = model.CanonicalModel.Provider.Id;
                CanonicalModel = model.CanonicalModel.Selector;
            }

            _main?.Session.UpdateSelection(_model, Mode);
            if (model is not null)
            {
                _main?.Session.UseResolvedSelection(model);
            }
        }

        Diagnostics.Write(new("session", "selection_updated", DiagnosticSeverity.Information));
    }

    // Indefinite by design. It ends when the caller stops listening, not when
    // a turn finishes -- and it does not wait for an agent session to exist, so
    // a client can subscribe before sending anything.
    public async IAsyncEnumerable<Event> Listen(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var events = _eventBroker.Subscribe();
        _ = Main();
        foreach (var scope in Registry.SnapshotScopes())
        {
            foreach (var published in QueueInventoryProtocol.Convert(scope.GetService<IAgentQueues>().CaptureInventory(), _mainSessionId))
            {
                yield return published;
            }

            foreach (var published in ShellProcessInventoryProtocol.Convert(scope.Processes.CaptureInventory()))
            {
                yield return published;
            }
        }

        yield return new Event { SessionUsageSnapshot = _eventRepository.GetRuntimeStatistics().CaptureUsage(_mainSessionId) };
        await foreach (var published in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return published;
        }
    }

    // The user talks to the user session; the main agent session is what
    // actually runs the turn. Admitting is not running it: it returns as soon
    // as the prompt is durable, whether or not a turn was already in flight.
    public async Task<Admission> SendText(
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
    public ValueTask DisposeAsync()
    {
        lock (_disposalGate)
        {
            _disposal ??= DisposeResources();
            return new ValueTask(_disposal);
        }
    }

    public IReadOnlyList<ActiveWorkObservation> ActiveWork() =>
        [.. Registry.SnapshotScopes().SelectMany(static scope => scope.Processes.Active()), .. Registry.Active(), .. Registry.SnapshotScopes().SelectMany(static scope => scope.AgentTaskRuns.Active())];

    public Task SetGoal(string goal, CancellationToken cancellationToken) =>
        MainScope().Goals.SetGoal(goal, cancellationToken);

    public void ClearGoal() => MainScope().Goals.ClearGoal();

    public Task Compact(ContextSize? targetContextSize, CancellationToken cancellationToken) =>
        Main().Compact(targetContextSize, cancellationToken);

    private async Task DisposeResources()
    {
        Diagnostics.Write(new("session", "closing", DiagnosticSeverity.Information));
        Exception? failure = null;

        try
        {
            await Task.WhenAll(Registry.SnapshotScopes().Select(static scope => scope.AgentTaskRuns.Settle())).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        ValueTask registryShutdown;
        try
        {
            registryShutdown = Registry.BeginShutdown();
        }
        catch (Exception exception)
        {
            failure ??= exception;
            registryShutdown = ValueTask.CompletedTask;
        }

        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            await registryShutdown.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        if (_main is not null)
        {
            try
            {
                await _main.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        foreach (var agent in _agents.Where(agent => !ReferenceEquals(agent, _main)))
        {
            try
            {
                await agent.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        Questions.Dispose();
        Permissions.Dispose();

        // Ends every subscription on this session's stream. A listener blocked
        // on MoveNext returns false rather than waiting forever.
        _lifetime.Dispose();
        _eventBroker.Dispose();
        _agents.Clear();
        Diagnostics.Write(new("session", "producers_stopped", failure is null ? DiagnosticSeverity.Information : DiagnosticSeverity.Error)
        {
            ErrorCode = failure is null ? null : DiagnosticEvent.ClassifyFailure(failure),
        });
        try
        {
            await _resources.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    // Built once after owner initialization. The lock also protects concurrent
    // access from RPC handlers throughout the session lifetime.
    private IAgentSessionScope MainScope()
    {
        lock (_mainGate)
        {
            return _main ?? throw new InvalidOperationException("the main agent session is not initialized");
        }
    }

    private IAgentSession Main() => MainScope().Session;

    private IAgentSession InitializeMain()
    {
        var scope = _agentSessions.Create(
            AgentIdentity.Main(_mainSessionId, _rootAgentName, _promptTemplates),
            AgentSessionParentLink.Root(),
            _model,
            _eventBroker,
            _agentSessions.PrepareHistory(_mainSessionId, _eventRepository),
            Mode,
            Mode.Profile.SecurityProfile,
            Status,
            Registry,
            _lifetime.Token);
        try
        {
            Registry.RegisterRootScope(scope);
            lock (_mainGate)
            {
                _main = scope;
                _agents.Add(scope);
                return _main.Session;
            }
        }
        catch
        {
            _agents.Add(scope);
            throw;
        }
    }
}
