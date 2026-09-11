using System.Runtime.ExceptionServices;
using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Diagnostics;
using Parrot.Process;
using Parrot.Questions;
using Parrot.Queues;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class TestAgentSessionScope : IAgentSessionScope, IDisposable
{
    private readonly Lock _gate = new();
    private readonly PromptTemplateCatalog _promptTemplates;
    private IAgentSession? _session;
    private Task? _shutdown;

    private TestAgentSessionScope(
        AgentIdentity owner,
        IAgentRegistry registry,
        AgentSessionParentLink parentLink,
        PromptTemplateCatalog promptTemplates,
        UserSessionResources resources,
        ProcessRunner runner,
        IDiagnosticLog diagnostics,
        CancellationToken lifetime)
    {
        _promptTemplates = promptTemplates;
        ChildRegistry = new ChildRegistry(owner);
        AgentTaskRuns = new AgentTaskRunCatalog(owner.SessionId, diagnostics, lifetime);
        Processes = new ShellProcessOwner(owner, resources, new AgentPathEnvironment(resources, resources.AgentScratch(owner.SessionId)), runner, diagnostics, lifetime);
        Queues = new AgentQueues(owner, parentLink.Parent?.Queues, resources, ChildRegistry, diagnostics);
        Queues.Initialize();
        ParentScope = AgentSessionParentScope.Bind(owner, registry, () => this, ChildRegistry, parentLink);
        AgentSpawner = new AgentSpawner(owner, registry, ParentScope, ChildRegistry);
        ChildQuestions = new ChildQuestionCoordinator(ParentScope, promptTemplates);
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

    public ShellProcessOwner Processes { get; }

    public AgentQueues Queues { get; }

    public AgentTaskRunCatalog AgentTaskRuns { get; }

    public GoalService? GoalsState { get; private set; }

    public GoalService Goals =>
        GoalsState ?? throw new InvalidOperationException("The agent session is not attached to its scope.");

    public AgentSpawner AgentSpawner { get; }

    public IChildRegistry ChildRegistry { get; }

    public AgentSessionParentScope ParentScope { get; }

    public ChildQuestionCoordinator ChildQuestions { get; }

    public static TestAgentSessionScope Build(
        AgentIdentity owner,
        AgentSessionParentLink parentLink,
        IAgentRegistry registry,
        PromptTemplateCatalog promptTemplates,
        Func<AgentSessionParentScope, IAgentSessionScope, IChildRegistry, ChildQuestionCoordinator, IAgentSession> buildSession) => BuildWithResources(owner, parentLink, registry, promptTemplates, TestModels.Resources(), new ProcessRunner(string.Empty), TestDiagnosticLog.Instance, buildSession, CancellationToken.None);

    public static TestAgentSessionScope BuildWithResources(
        AgentIdentity owner,
        AgentSessionParentLink parentLink,
        IAgentRegistry registry,
        PromptTemplateCatalog promptTemplates,
        UserSessionResources resources,
        ProcessRunner runner,
        IDiagnosticLog diagnostics,
        Func<AgentSessionParentScope, IAgentSessionScope, IChildRegistry, ChildQuestionCoordinator, IAgentSession> buildSession,
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
            Queues.Attach(session);
        }
    }

    public void PublishInventories()
    {
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
            await AgentTaskRuns.DisposeAsync().ConfigureAwait(false);
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
            await Processes.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        Queues.Dispose();
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
