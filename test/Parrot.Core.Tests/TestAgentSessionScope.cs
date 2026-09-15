using System.Runtime.ExceptionServices;
using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Diagnostics;
using Parrot.Events;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.Questions;
using Parrot.Queues;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class TestAgentSessionScope : IAgentSessionScope, IDisposable
{
    private readonly Lock _gate = new();
    private readonly AgentSessionServices _services = new();
    private readonly IEventBroker _events = new EventBroker();
    private readonly IAgentQueues _queues;
    private readonly IProcessOwner _processes;
    private readonly IAgentTaskRunCatalog _agentTaskRuns;
    private readonly IChildQuestion _childQuestion;
    private readonly IReadOnlyList<IInventoryPublisher> _publishers;
    private readonly IPromptTemplateCatalog _promptTemplates;
    private readonly AgentSessionParentLink _parentLink;
    private IAgentSession? _session;
    private Task? _shutdown;

    private TestAgentSessionScope(
        AgentIdentity owner,
        IAgentRegistry registry,
        AgentSessionParentLink parentLink,
        IPromptTemplateCatalog promptTemplates,
        UserSessionResources resources,
        ProcessRunner runner,
        IDiagnosticLog diagnostics,
        CancellationToken lifetime)
    {
        _promptTemplates = promptTemplates;
        _parentLink = parentLink;
        ChildRegistry = new ChildRegistry(owner, QueueChildAdmissionValidator.Validate);
        _agentTaskRuns = new AgentTaskRunCatalog(owner.SessionId, diagnostics, lifetime);
        _childQuestion = new ChildQuestion(owner);
        _processes = new ShellProcessOwner(owner, resources, new AgentPathEnvironment(resources, resources.AgentScratch(owner.SessionId)), runner, diagnostics, lifetime);
        _queues = new AgentQueues(owner, parentLink.Parent?.GetService<IAgentQueues>(), resources, ChildRegistry, static queueIdentity => new QueueInventory(queueIdentity), diagnostics);
        var root = parentLink.Parent;
        while (root?.ParentScope.Parent is { } parent)
        {
            root = parent;
        }

        _publishers = [new QueueSnapshotPublisher(_queues, _events, root?.Session.SessionId ?? owner.SessionId), new ProcessSnapshotPublisher(_processes, _events)];
        _services.Register<IAgentQueues>(_queues);
        _services.Register<IProcessOwner>(_processes);
        _services.Register<IAgentTaskRunCatalog>(_agentTaskRuns);
        _services.Register<IChildQuestion>(_childQuestion);
        _queues.Initialize();
        ParentScope = AgentSessionParentScope.Bind(owner, registry, () => this, ChildRegistry, parentLink);
        AgentSpawner = new AgentSpawner(owner, registry, ParentScope, ChildRegistry);
        ChildQuestions = new ChildQuestionCoordinator(ParentScope, ChildRegistry, promptTemplates);
    }

    public IAgentSession Session
    {
        get
        {
            lock (_gate)
            {
                return _session ?? throw new InvalidOperationException("The agent session is not attached to its scope.");
            }
        }
    }

    public IGoalService? GoalsState { get; private set; }

    public IGoalService Goals =>
        GoalsState ?? throw new InvalidOperationException("The agent session is not attached to its scope.");

    public IAgentSpawner AgentSpawner { get; }

    public IChildRegistry ChildRegistry { get; }

    public IAgentParentScope ParentScope { get; }

    public IChildQuestionCoordinator ChildQuestions { get; }

    public static TestAgentSessionScope Build(
        AgentIdentity owner,
        AgentSessionParentLink parentLink,
        IAgentRegistry registry,
        IPromptTemplateCatalog promptTemplates,
        Func<IAgentParentScope, IAgentSessionScope, IChildRegistry, IChildQuestionCoordinator, IAgentSession> buildSession) => BuildWithResources(owner, parentLink, registry, promptTemplates, TestModels.Resources(), new ProcessRunner(string.Empty), TestDiagnosticLog.Instance, buildSession, CancellationToken.None);

    public static TestAgentSessionScope BuildWithResources(
        AgentIdentity owner,
        AgentSessionParentLink parentLink,
        IAgentRegistry registry,
        IPromptTemplateCatalog promptTemplates,
        UserSessionResources resources,
        ProcessRunner runner,
        IDiagnosticLog diagnostics,
        Func<IAgentParentScope, IAgentSessionScope, IChildRegistry, IChildQuestionCoordinator, IAgentSession> buildSession,
        CancellationToken lifetime)
    {
        var scope = new TestAgentSessionScope(owner, registry, parentLink, promptTemplates, resources, runner, diagnostics, lifetime);
        try
        {
            scope.AttachSession(buildSession(scope.ParentScope, scope, scope.ChildRegistry, scope.ChildQuestions));
            return scope;
        }
        catch
        {
            scope.DisposeRejectedConstruction();
            throw;
        }
    }

    public T GetService<T>()
        where T : class => _services.GetService<T>();

    public void AttachSession(IAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            if (_session is not null)
            {
                throw new InvalidOperationException("The agent session is already attached to its scope.");
            }

            ObjectDisposedException.ThrowIf(_shutdown is not null, this);
            ParentScope.Validate(session.Identity);
            _session = session;
            ParentScope.ValidateOwnerScope(this);
            GoalsState = new GoalService(session, _promptTemplates);
            _queues.Attach(session);
        }
    }

    public void PublishSnapshots()
    {
    }

    public IReadOnlyList<Event> CaptureSnapshotEvents() =>
        [.. _publishers.SelectMany(static publisher => publisher.CaptureSnapshotEvents())];

    public async Task SettleWork()
    {
        await _agentTaskRuns.Settle().ConfigureAwait(false);
        await _processes.Settle().ConfigureAwait(false);
    }

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _shutdown ??= ShutDown();
            return new ValueTask(_shutdown);
        }
    }

    private void DisposeRejectedConstruction()
    {
        ChildQuestions.Dispose();
        _shutdown = DisposeComponents();
    }

    private async Task DisposeComponents()
    {
        Exception? failure = null;
        try
        {
            await _agentTaskRuns.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await ChildRegistry.DisposeChildren().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            await AgentSpawner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            if (_session is { } session)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            await _processes.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        _queues.Dispose();
        _events.Dispose();
        _childQuestion.Close();
        _parentLink.ReleaseRetention();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private async Task ShutDown()
    {
        Exception? failure = null;
        try
        {
            await DisposeComponents().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            ChildQuestions.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
