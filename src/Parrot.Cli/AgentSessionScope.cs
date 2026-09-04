using System.Runtime.ExceptionServices;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Questions;
using Parrot.Queues;

namespace Parrot.Cli;

internal sealed class AgentSessionScope : IAgentSessionScope
{
    private readonly Lock _gate = new();
    private readonly AgentQueues _queues;
    private IAgentSession? _session;
    private Task? _shutdown;

    internal AgentSessionScope(
        AgentIdentity owner,
        IAgentRegistry registry,
        PromptTemplateCatalog promptTemplates,
        AgentQueues queues)
    {
        _queues = queues;
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

    public IChildRegistry ChildRegistry { get; }

    public ChildQuestionCoordinator ChildQuestions { get; }

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

    internal void DisposeRejectedConstruction()
    {
        ChildQuestions.Dispose();
        _queues.Dispose();
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

        try
        {
            _queues.Dispose();
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
