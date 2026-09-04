using System.Runtime.ExceptionServices;
using Parrot.Config;
using Parrot.Questions;

namespace Parrot.Agent;

internal sealed class AgentSessionDirectScope : IAgentSessionScope
{
    private readonly Lock _gate = new();
    private IAgentSession? _session;
    private Task? _shutdown;

    private AgentSessionDirectScope(
        AgentIdentity owner,
        AgentRegistry registry,
        PromptTemplateCatalog promptTemplates)
    {
        ChildRegistry = new ChildRegistry(owner, registry);
        ChildQuestions = new ChildQuestionCoordinator(ChildRegistry, promptTemplates);
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

    public ChildRegistry ChildRegistry { get; }

    public ChildQuestionCoordinator ChildQuestions { get; }

    public static AgentSessionDirectScope Build(
        AgentIdentity owner,
        AgentSessionParentScope parentScope,
        AgentRegistry registry,
        PromptTemplateCatalog promptTemplates,
        Func<AgentSessionParentScope, IAgentSessionScope, ChildRegistry, ChildQuestionCoordinator, IAgentSession> buildSession)
    {
        var scope = new AgentSessionDirectScope(owner, registry, promptTemplates);
        try
        {
            scope.AttachSession(buildSession(parentScope, scope, scope.ChildRegistry, scope.ChildQuestions));
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
            _session = session;
        }
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
