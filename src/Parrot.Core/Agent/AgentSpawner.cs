using System.Runtime.ExceptionServices;
using System.Text;

namespace Parrot.Agent;

internal sealed class AgentSpawner : IAsyncDisposable
{
    private const int MaxDepth = 4;
    private readonly AgentIdentity _owner;
    private readonly IAgentRegistry _authority;
    private readonly AgentSessionParentScope _parentSessionScope;
    private readonly IChildRegistry _children;
    private readonly CancellationTokenSource _lifetime;
    private readonly Lock _gate = new();
    private readonly Lock _spawnGate = new();
    private readonly Dictionary<string, RetainedAgentReservation> _retainedAgents = new(StringComparer.Ordinal);
    private readonly List<Task> _rejectedScopeDisposals = [];
    private bool _accepting = true;
    private int _pendingConstructions;
    private TaskCompletionSource? _constructionsSettled;
    private Task? _shutdown;

    internal AgentSpawner(
        AgentIdentity owner,
        IAgentRegistry authority,
        AgentSessionParentScope parentSessionScope,
        IChildRegistry children)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _parentSessionScope = parentSessionScope ?? throw new ArgumentNullException(nameof(parentSessionScope));
        _children = children ?? throw new ArgumentNullException(nameof(children));
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(authority.ChildLifetime);
    }

    public IAgentSessionScope SpawnScope(AgentLaunchRequest request)
    {
        var conflictingNames = 0;
        while (true)
        {
            try
            {
                return SpawnScopeCandidate(request, conflictingNames);
            }
            catch (ChildNameConflictException)
            {
                conflictingNames++;
            }
        }
    }

    public IAgentSessionScope GetOrSpawnScope(string requestedName, Func<AgentLaunchRequest> selectLaunch)
    {
        lock (_spawnGate)
        {
            lock (_gate)
            {
                EnsureAccepting();
            }

            var name = Sanitize(requestedName);
            var existing = _children.FindNamedChildScope(name);
            if (existing is not null)
            {
                return existing;
            }

            try
            {
                return SpawnScopeCandidate(selectLaunch(), 0);
            }
            catch (ChildNameConflictException)
            {
                return _children.ResolveNamedChildScope(name);
            }
        }
    }

    public void ReleaseRetainedAgent(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        RetainedAgentReservation? reservation;
        lock (_gate)
        {
            if (!_retainedAgents.Remove(sessionId, out reservation))
            {
                throw new AgentRegistryException($"retained child agent not found: {sessionId}");
            }
        }

        reservation.Release();
    }

    public ValueTask DisposeAsync()
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

    private static string Sanitize(string name)
    {
        var sanitized = new StringBuilder(name.Length);
        var separator = false;

        foreach (var character in name)
        {
            if (character is >= 'A' and <= 'Z')
            {
                _ = sanitized.Append(char.ToLowerInvariant(character));
                separator = false;
            }
            else if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                _ = sanitized.Append(character);
                separator = false;
            }
            else if (sanitized.Length > 0)
            {
                separator = true;
            }

            if (separator && sanitized.Length > 0 && sanitized[^1] != '-')
            {
                _ = sanitized.Append('-');
            }
        }

        return sanitized.ToString().TrimEnd('-');
    }

    private static string SelectName(string requestedName, string sessionId, int conflictingNames)
    {
        var basis = Sanitize(requestedName);
        if (basis.Length == 0)
        {
            return $"agent-{sessionId[^6..]}";
        }

        return conflictingNames == 0 ? basis : $"{basis}-{conflictingNames + 1}";
    }

    private IAgentSessionScope SpawnScopeCandidate(AgentLaunchRequest request, int conflictingNames)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Parent);
        ArgumentNullException.ThrowIfNull(request.Selection);
        ArgumentNullException.ThrowIfNull(request.RequestedScope);

        if (!ReferenceEquals(request.Parent.Identity, _owner))
        {
            throw new AgentRegistryException(
                $"launch parent does not match child registry owner: expected {_owner.SessionId}, actual {request.Parent.SessionId}");
        }

        var depth = _owner.Depth + 1;
        if (depth > MaxDepth)
        {
            throw new AgentRegistryException("subagent depth limit reached");
        }

        var registeredOwnerScope = _parentSessionScope.RequireOwnerScope();
        var profile = _authority.ResolveChildProfile(request.RequestedProfile);
        var status = _authority.RequireStatus();
        var retainedReservation = _authority.ReserveRetainedAgent();
        AgentIdentity childIdentity;
        AgentSessionParentLink childParentLink;

        try
        {
            lock (_gate)
            {
                EnsureAccepting();
                childParentLink = new AgentSessionParentLink(registeredOwnerScope, request.DeliveryPolicy);
                if (childParentLink.PolicyLineage.CountProfile(profile.Id) >= profile.RecursionLimit)
                {
                    throw new AgentRegistryException("subagent profile recursion limit reached");
                }

                var sessionId = Identifier.AgentSession();
                var name = SelectName(request.RequestedName, sessionId, conflictingNames);
                var scope = _owner.Scope.DeriveChild(name, depth, request.RequestedScope);
                childIdentity = AgentIdentity.ChildWithPolicyLineage(
                    sessionId,
                    _owner.SessionId,
                    _owner.Name,
                    name,
                    depth,
                    scope,
                    childParentLink.PolicyLineage,
                    _owner.PromptTemplates);
                _pendingConstructions++;
            }
        }
        catch
        {
            retainedReservation.Rollback();
            throw;
        }

        var historyInitialized = false;
        IAgentSessionScope? constructedScope = null;
        try
        {
            _authority.InitializeChildHistory(
                _owner.SessionId,
                childIdentity.SessionId,
                request.Boundary,
                request.Fork);
            historyInitialized = true;
            var securityProfile = childParentLink.PolicyLineage.Resolve(profile.SecurityProfile);
            constructedScope = _authority.CreateChildScope(
                childIdentity,
                childParentLink,
                request.Model,
                new NoopMode(profile, securityProfile),
                securityProfile,
                status,
                _lifetime.Token);
            Retain(childIdentity.SessionId, retainedReservation);
            if (!_children.TryAdd(constructedScope))
            {
                throw new AgentRegistryException("the user session is shutting down");
            }

            return constructedScope;
        }
        catch
        {
            RejectConstruction(childIdentity.SessionId, retainedReservation, constructedScope, historyInitialized);
            throw;
        }
        finally
        {
            CompleteConstruction();
        }
    }

    private void Retain(string sessionId, RetainedAgentReservation reservation)
    {
        lock (_gate)
        {
            EnsureAccepting();
            _retainedAgents.Add(sessionId, reservation);
            reservation.Commit();
        }
    }

    private void Reject(string sessionId, RetainedAgentReservation reservation)
    {
        bool retained;
        lock (_gate)
        {
            retained = _retainedAgents.Remove(sessionId);
        }

        if (retained)
        {
            reservation.Release();
        }
        else
        {
            reservation.Rollback();
        }
    }

    private void RejectConstruction(
        string sessionId,
        RetainedAgentReservation reservation,
        IAgentSessionScope? constructedScope,
        bool historyInitialized)
    {
        Reject(sessionId, reservation);
        if (constructedScope is not null)
        {
            var rejectedScopeDisposal = constructedScope.DisposeAsync().AsTask();
            lock (_gate)
            {
                _rejectedScopeDisposals.Add(rejectedScopeDisposal);
            }
        }

        if (historyInitialized)
        {
            _authority.CleanupChildHistory(sessionId);
        }
    }

    private void CompleteConstruction()
    {
        TaskCompletionSource? settled = null;
        lock (_gate)
        {
            _pendingConstructions--;
            if (_pendingConstructions == 0)
            {
                settled = _constructionsSettled;
                _constructionsSettled = null;
            }
        }

        _ = settled?.TrySetResult();
    }

    private async Task ShutDown()
    {
        await Task.Yield();
        Task pendingConstructions;
        lock (_gate)
        {
            if (_pendingConstructions == 0)
            {
                pendingConstructions = Task.CompletedTask;
            }
            else
            {
                _constructionsSettled ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                pendingConstructions = _constructionsSettled.Task;
            }
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        await pendingConstructions.ConfigureAwait(false);
        Task[] rejectedScopeDisposals;
        lock (_gate)
        {
            rejectedScopeDisposals = [.. _rejectedScopeDisposals];
            _rejectedScopeDisposals.Clear();
        }

        Exception? failure = null;
        try
        {
            await Task.WhenAll(rejectedScopeDisposals).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        RetainedAgentReservation[] retainedAgents;
        lock (_gate)
        {
            retainedAgents = [.. _retainedAgents.Values];
            _retainedAgents.Clear();
        }

        foreach (var retainedAgent in retainedAgents)
        {
            retainedAgent.Release();
        }

        _lifetime.Dispose();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private void EnsureAccepting()
    {
        if (!_accepting || !_authority.IsAccepting || !_children.IsAccepting)
        {
            throw new AgentRegistryException("the user session is shutting down");
        }
    }
}
