using System.Runtime.ExceptionServices;
using Parrot.AgentTasks;
using Parrot.Events;
using Parrot.Process;
using Parrot.Questions;
using Parrot.Queues;

namespace Parrot.Agent;

internal sealed class AgentSessionScope : IAgentSessionScope
{
    private readonly AgentSessionComposition _composition;
    private readonly Parrot.Diagnostics.IDiagnosticLog _diagnostics;
    private readonly string _sessionId;
    private readonly Lock _gate = new();
    private readonly AgentSessionServices _services = new();
    private readonly IReadOnlyList<IInventoryPublisher> _publishers;
    private readonly IReadOnlyList<IAgentWorkOwner> _workOwners;
    private Task[]? _publications;
    private Task? _disposal;

    internal AgentSessionScope(
        AgentSessionScopeArguments arguments,
        Func<AgentSessionScopeArguments, IAgentSessionScope, AgentSessionComposition> composeSession)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        _diagnostics = arguments.Diagnostics;
        _sessionId = arguments.Identity.SessionId;
        _composition = composeSession(arguments, this);
        try
        {
            Session = _composition.Session;
            _publishers = _composition.Publishers;
            _workOwners = _composition.WorkOwners;
            _services.Register<IAgentQueues>(_composition.Queues);
            _services.Register<IProcessOwner>(_composition.Processes);
            _services.Register<IAgentTaskRunCatalog>(_composition.AgentTaskRuns);
            ChildRegistry = _composition.ChildRegistry;
            ParentScope = _composition.ParentScope;
            ChildQuestions = _composition.ChildQuestions;
            AgentSpawner = _composition.AgentSpawner;
            Goals = _composition.Goals;
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

    public void PublishSnapshots()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            _publications ??= [.. _publishers.Select(static publisher => publisher.Run())];
        }
    }

    public IReadOnlyList<Protocol.Event> CaptureSnapshotEvents() =>
        [.. _publishers.SelectMany(static publisher => publisher.CaptureSnapshotEvents())];

    public async Task SettleWork()
    {
        Exception? failure = null;
        foreach (var owner in _workOwners)
        {
            try
            {
                await owner.Settle().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
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
                    await SettleWork().ConfigureAwait(false);
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
                        _composition.Queues.Dispose();
                        await Task.WhenAll(_publications ?? []).ConfigureAwait(false);
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
