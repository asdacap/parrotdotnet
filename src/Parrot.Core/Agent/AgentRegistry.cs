using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Parrot.Config;
using Parrot.Diagnostics;
using Parrot.Events;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed class AgentRegistry(
    IAgentSessionFactory agentSessions,
    IEventBroker eventBroker,
    IEventRepository eventRepository,
    ProfileRegistry profiles,
    IPromptTemplateCatalog promptTemplates,
    RetainedAgentBudget retainedAgents,
    IDiagnosticLog diagnostics,
    CancellationToken lifetime) : IAgentRegistry
{
    private readonly RetainedAgentBudget _retainedAgents = retainedAgents
        ?? throw new ArgumentNullException(nameof(retainedAgents));

    private readonly Dictionary<string, IAgentSessionScope> _roots = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private readonly Lock _gate = new();

    private bool _accepting = true;
    private IRuntimeStatus? _status;
    private Task? _shutdown;

    public CancellationToken ChildLifetime => _lifetime.Token;

    public IPromptTemplateCatalog PromptTemplates => promptTemplates;

    public bool IsAccepting
    {
        get
        {
            lock (_gate)
            {
                return _accepting;
            }
        }
    }

    public void AttachStatus(IRuntimeStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        lock (_gate)
        {
            _status = _status is null
                ? status
                : throw new AgentRegistryException("the runtime status is already attached");
        }
    }

    public void RegisterRootScope(IAgentSessionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.Session.Depth != 0 || scope.Session.ParentSessionId.Length != 0)
        {
            throw new AgentRegistryException("only the root agent scope may be registered directly");
        }

        lock (_gate)
        {
            EnsureAccepting();
            var sessionId = scope.Session.SessionId;
            if (_roots.ContainsKey(sessionId)
                || _roots.Values.Any(registered => ReferenceEquals(registered.Session, scope.Session)))
            {
                throw new AgentRegistryException($"agent scope identity is already registered: {sessionId}");
            }

            _roots.Add(sessionId, scope);
            try
            {
                scope.PublishSnapshots();
            }
            catch
            {
                _ = _roots.Remove(sessionId);
                throw;
            }
        }
    }

    public void UnregisterRootScope(IAgentSessionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        lock (_gate)
        {
            if (!_roots.TryGetValue(scope.Session.SessionId, out var registered)
                || !ReferenceEquals(registered, scope)
                || scope.Session.Depth != 0)
            {
                throw new AgentRegistryException($"agent scope not found: {scope.Session.SessionId}");
            }

            _ = _roots.Remove(scope.Session.SessionId);
        }
    }

    public IReadOnlyList<IAgentSessionScope> SnapshotScopes() =>
        [.. SnapshotRoots().SelectMany(EnumerateScopes)];

    public IReadOnlyList<ActiveWorkObservation> Active()
    {
        var sessions = SnapshotDescendants();
        return [.. sessions
            .Where(static session => session.IsActive())
            .Select(static session => new ActiveWorkObservation(
                session.SessionId,
                session.Name,
                ActiveWorkKind.Agent,
                ActiveWorkState.Running))
            .OrderBy(static observation => observation.Id, StringComparer.Ordinal)];
    }

    public IReadOnlyList<ActiveAgentSnapshot> ActiveSnapshot()
    {
        var snapshots = SnapshotDescendants()
            .Where(static session => session.IsActive())
            .Select(static session => new ActiveAgentSnapshot(
                session.SessionId,
                session.ParentSessionId,
                session.Name))
            .OrderBy(static snapshot => snapshot.SessionId, StringComparer.Ordinal)
            .ToArray();
        return Array.AsReadOnly(snapshots);
    }

    public ValueTask BeginShutdown()
    {
        lock (_gate)
        {
            if (_shutdown is null)
            {
                _accepting = false;
                _shutdown = ShutDown();
            }

            return new ValueTask(_shutdown);
        }
    }

    public ValueTask DisposeAsync() => BeginShutdown();

    public IAgentProfile ResolveChildProfile(string profileId) => profiles.ResolveChild(profileId);

    public RetainedAgentReservation ReserveRetainedAgent()
    {
        lock (_gate)
        {
            EnsureAccepting();
            return _retainedAgents.Reserve();
        }
    }

    public IRuntimeStatus RequireStatus()
    {
        lock (_gate)
        {
            EnsureAccepting();
            return _status ?? throw new AgentRegistryException("the runtime status is not attached");
        }
    }

    public bool ContainsScope(IAgentSessionScope candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        foreach (var root in SnapshotRoots())
        {
            if (ReferenceEquals(root, candidate) || root.ChildRegistry.ContainsDescendantScope(candidate))
            {
                return true;
            }
        }

        return false;
    }

    public IAgentSessionScope? FindScope(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        foreach (var root in SnapshotRoots())
        {
            if (string.Equals(root.Session.SessionId, sessionId, StringComparison.Ordinal))
            {
                return root;
            }

            var descendant = root.ChildRegistry.FindDescendantScope(sessionId);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    public IAgentSessionScope CreateChildScope(
        AgentIdentity identity,
        AgentSessionParentLink parentLink,
        Llm.ModelSelector model,
        IMode mode,
        Security.SecurityProfile securityProfile,
        IRuntimeStatus status,
        IEventRepository childHistory,
        CancellationToken childLifetime)
    {
        var started = Stopwatch.GetTimestamp();
        diagnostics.Write(new DiagnosticEvent("agent", "child_scope_start", DiagnosticSeverity.Information)
        {
            AgentSessionId = identity.SessionId,
            CorrelationId = identity.ParentSessionId,
        });
        try
        {
            var scope = agentSessions.Create(
                identity,
                parentLink,
                model,
                eventBroker,
                childHistory,
                mode,
                securityProfile,
                status,
                this,
                childLifetime);
            diagnostics.Write(new DiagnosticEvent("agent", "child_scope_completed", DiagnosticSeverity.Information)
            {
                AgentSessionId = identity.SessionId,
                CorrelationId = identity.ParentSessionId,
                Outcome = "succeeded",
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            });
            return scope;
        }
        catch (Exception failure)
        {
            diagnostics.Write(new DiagnosticEvent("agent", "child_scope_completed", failure is OperationCanceledException ? DiagnosticSeverity.Information : DiagnosticSeverity.Error)
            {
                AgentSessionId = identity.SessionId,
                CorrelationId = identity.ParentSessionId,
                Outcome = failure is OperationCanceledException ? "cancelled" : "failed",
                ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            });
            throw;
        }
    }

    public IEventRepository InitializeChildHistory(
        string parentSessionId,
        string childSessionId,
        HistoryForkBoundary boundary,
        HistoryForkSelection fork)
    {
        var childHistory = agentSessions.PrepareHistory(childSessionId, eventRepository);
        childHistory.InitializeForkedAgentHistory(parentSessionId, childSessionId, boundary, fork);
        return childHistory;
    }

    private static IEnumerable<IAgentSessionScope> EnumerateScopes(IAgentSessionScope scope) =>
        scope.ChildRegistry.SnapshotChildScopes().SelectMany(EnumerateScopes).Prepend(scope);

    private IAgentSessionScope[] SnapshotRoots()
    {
        lock (_gate)
        {
            return _accepting ? [.. _roots.Values] : [];
        }
    }

    private IAgentSession[] SnapshotDescendants() =>
        [.. SnapshotRoots().SelectMany(static root => root.ChildRegistry.SnapshotDescendants())];

    private void EnsureAccepting()
    {
        if (!_accepting)
        {
            throw new AgentRegistryException("the user session is shutting down");
        }
    }

    private async Task ShutDown()
    {
        await Task.Yield();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        IAgentSessionScope[] roots;
        lock (_gate)
        {
            roots = [.. _roots.Values];
        }

        Exception? failure = null;
        foreach (var root in roots)
        {
            try
            {
                await root.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        _lifetime.Dispose();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
