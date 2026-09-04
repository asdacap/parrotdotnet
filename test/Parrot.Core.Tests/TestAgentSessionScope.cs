using System.Runtime.ExceptionServices;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Questions;

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
        PromptTemplateCatalog promptTemplates)
    {
        _promptTemplates = promptTemplates;
        ChildRegistry = new ChildRegistry(owner, registry, () => this);
        ParentScope = AgentSessionParentScope.Bind(owner, registry, () => this, ChildRegistry, parentLink);
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

    public GoalService? GoalsState { get; private set; }

    public GoalService Goals =>
        GoalsState ?? throw new InvalidOperationException("The agent session is not attached to its scope.");

    public IChildRegistry ChildRegistry { get; }

    public AgentSessionParentScope ParentScope { get; }

    public ChildQuestionCoordinator ChildQuestions { get; }

    public static TestAgentSessionScope Build(
        AgentIdentity owner,
        AgentSessionParentLink parentLink,
        IAgentRegistry registry,
        PromptTemplateCatalog promptTemplates,
        Func<AgentSessionParentScope, IAgentSessionScope, IChildRegistry, ChildQuestionCoordinator, IAgentSession> buildSession)
    {
        var scope = new TestAgentSessionScope(owner, registry, parentLink, promptTemplates);
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
            ChildRegistry.ValidateOwner(session.Identity);
            ParentScope.Validate(session.Identity);
            _session = session;
            ParentScope.ValidateOwnerScope(this);
            GoalsState = new GoalService(session, _promptTemplates);
        }
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
        _shutdown = ChildRegistry.DisposeAsync().AsTask();
    }

    private async Task ShutDown()
    {
        Exception? failure = null;
        try
        {
            await ChildRegistry.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await Session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
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
