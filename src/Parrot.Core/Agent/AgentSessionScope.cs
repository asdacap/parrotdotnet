using Parrot.AgentTasks;
using Parrot.Process;
using Parrot.Questions;
using Parrot.Queues;

namespace Parrot.Agent;

internal sealed class AgentSessionScope : IAgentSessionScope
{
    private readonly AgentSessionComposition _composition;
    private readonly Parrot.Diagnostics.IDiagnosticLog _diagnostics;
    private readonly string _sessionId;
    private readonly AgentSessionScopeArguments _arguments;
    private readonly Lock _gate = new();
    private readonly AgentSessionServices _services = new();
    private readonly IAgentQueues _queues;
    private Task? _publication;
    private Task? _disposal;

    internal AgentSessionScope(AgentSessionScopeArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        _arguments = arguments;
        _diagnostics = arguments.Diagnostics;
        _sessionId = arguments.Identity.SessionId;
        _composition = new AgentSessionComposition(arguments, this);
        try
        {
            Session = _composition.Session;
            Processes = _composition.Processes;
            _queues = _composition.Queues;
            _services.Register<IAgentQueues>(_queues);
            ChildRegistry = _composition.ChildRegistry;
            ParentScope = _composition.ParentScope;
            ChildQuestions = _composition.ChildQuestions;
            AgentSpawner = _composition.AgentSpawner;
            Goals = _composition.Goals;
            AgentTaskRuns = _composition.AgentTaskRuns;
            ParentScope.Validate(Session.Identity);
            ParentScope.ValidateOwnerScope(this);
            _diagnostics.Write(new("agent", "created", Parrot.Diagnostics.DiagnosticSeverity.Information)
            {
                AgentSessionId = _sessionId,
            });
        }
        catch
        {
            _composition.Dispose();
            throw;
        }
    }

    public IAgentSession Session { get; }

    public IGoalService Goals { get; }

    public IAgentSpawner AgentSpawner { get; }

    public IChildRegistry ChildRegistry { get; }

    public IAgentParentScope ParentScope { get; }

    public IChildQuestionCoordinator ChildQuestions { get; }

    public IProcessOwner Processes { get; }

    public IAgentTaskRunCatalog AgentTaskRuns { get; }

    public T GetService<T>()
        where T : class => _services.GetService<T>();

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _disposal ??= DisposeResources();
            return new ValueTask(_disposal);
        }
    }

    public void PublishInventories()
    {
        var root = (IAgentSessionScope)this;
        while (root.ParentScope.Parent is { } parent)
        {
            root = parent;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            _publication ??= new AgentInventoryPublisher(Processes, _queues, _arguments.EventBroker, root.Session.SessionId).Run();
        }
    }

    internal void DisposeRejectedConstruction() => _composition.Dispose();

    private async Task DisposeResources()
    {
        try
        {
            try
            {
                try
                {
                    try
                    {
                        await AgentTaskRuns.Settle().ConfigureAwait(false);
                    }
                    finally
                    {
                        await Processes.Settle().ConfigureAwait(false);
                    }
                }
                finally
                {
                    await Session.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    try
                    {
                        await AgentSpawner.DisposeAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        await ChildRegistry.DisposeChildren().ConfigureAwait(false);
                    }
                }
                finally
                {
                    try
                    {
                        await _composition.DisposeAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        _queues.Dispose();
                        await Processes.DisposeAsync().ConfigureAwait(false);
                        await (_publication ?? Task.CompletedTask).WaitAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }

            _diagnostics.Write(new("agent", "closed", Parrot.Diagnostics.DiagnosticSeverity.Information)
            {
                AgentSessionId = _sessionId,
            });
        }
        catch (Exception failure)
        {
            _diagnostics.Write(new("agent", "close_failed", Parrot.Diagnostics.DiagnosticSeverity.Error)
            {
                AgentSessionId = _sessionId,
                ErrorCode = Parrot.Diagnostics.DiagnosticEvent.ClassifyFailure(failure),
            });
            throw;
        }
    }
}
