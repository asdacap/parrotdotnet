using System.Runtime.ExceptionServices;
using System.Text;

namespace Parrot.Agent;

internal sealed class AgentSpawner : IAsyncDisposable
{
    private const int MaxDepth = 4;
    private readonly AgentIdentity _owner;
    private readonly IAgentRegistry _authority;
    private readonly IAgentSessionScope _ownerScope;
    private readonly IChildRegistry _children;
    private readonly CancellationTokenSource _lifetime;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _pendingProfiles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RetainedAgentReservation> _retainedAgents = new(StringComparer.Ordinal);
    private readonly List<Task> _rejectedScopeDisposals = [];
    private bool _accepting = true;
    private int _pendingConstructions;
    private TaskCompletionSource? _constructionsSettled;
    private Task? _shutdown;

    internal AgentSpawner(
        AgentIdentity owner,
        IAgentRegistry authority,
        IAgentSessionScope ownerScope,
        IChildRegistry children)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _ownerScope = ownerScope ?? throw new ArgumentNullException(nameof(ownerScope));
        _children = children ?? throw new ArgumentNullException(nameof(children));
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(authority.ChildLifetime);
    }

    public IAgentSessionScope SpawnScope(AgentLaunchRequest request)
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

        var registeredOwnerScope = RequireOwnerScope();
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
                if (!ReferenceEquals(_ownerScope, registeredOwnerScope))
                {
                    throw new AgentRegistryException($"parent agent scope not found: {_owner.SessionId}");
                }

                childParentLink = new AgentSessionParentLink(registeredOwnerScope, request.DeliveryPolicy);
                if (childParentLink.PolicyLineage.CountProfile(profile.Id)
                    + _pendingProfiles.GetValueOrDefault(profile.Id)
                    >= profile.RecursionLimit)
                {
                    throw new AgentRegistryException("subagent profile recursion limit reached");
                }

                var sessionId = Identifier.AgentSession();
                var name = SelectUniqueName(request.RequestedName, sessionId);
                _ = _pendingNames.Add(name);
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
                _pendingProfiles[profile.Id] = _pendingProfiles.GetValueOrDefault(profile.Id) + 1;
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
            _children.Add(constructedScope);
            return constructedScope;
        }
        catch
        {
            Reject(childIdentity.SessionId, retainedReservation);
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
                _authority.CleanupChildHistory(childIdentity.SessionId);
            }

            throw;
        }
        finally
        {
            CompleteConstruction(childIdentity, profile.Id);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_shutdown is null)
            {
                _accepting = false;
                _shutdown = ShutDown(_children.TakeAll());
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

    private IAgentSessionScope RequireOwnerScope()
    {
        if (!_authority.IsAccepting)
        {
            throw new AgentRegistryException("the user session is shutting down");
        }

        lock (_gate)
        {
            EnsureAccepting();
        }

        return _authority.ContainsScope(_ownerScope)
            ? _ownerScope
            : throw new AgentRegistryException($"parent agent scope not found: {_owner.SessionId}");
    }

    private void Release(string sessionId)
    {
        RetainedAgentReservation? reservation;
        lock (_gate)
        {
            if (!_retainedAgents.Remove(sessionId, out reservation))
            {
                throw new InvalidOperationException($"Retained agent reservation not found: {sessionId}");
            }
        }

        reservation.Release();
    }

    private string SelectUniqueName(string requestedName, string sessionId)
    {
        var basis = Sanitize(requestedName);
        if (basis.Length == 0)
        {
            basis = $"agent-{sessionId[^6..]}";
        }

        var candidate = basis;
        var suffix = 2;
        while (_children.ContainsName(candidate) || _pendingNames.Contains(candidate))
        {
            candidate = $"{basis}-{suffix}";
            suffix++;
        }

        return candidate;
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

    private void CompleteConstruction(AgentIdentity childIdentity, string profileId)
    {
        TaskCompletionSource? settled = null;
        lock (_gate)
        {
            _pendingConstructions--;
            if (_pendingProfiles.GetValueOrDefault(profileId) == 1)
            {
                _ = _pendingProfiles.Remove(profileId);
            }
            else
            {
                _pendingProfiles[profileId]--;
            }

            _ = _pendingNames.Remove(childIdentity.Name);
            if (_pendingConstructions == 0)
            {
                settled = _constructionsSettled;
                _constructionsSettled = null;
            }
        }

        _ = settled?.TrySetResult();
    }

    private async Task ShutDown(IReadOnlyList<IAgentSessionScope> children)
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

        for (var index = children.Count - 1; index >= 0; index--)
        {
            var child = children[index];
            var sessionId = child.Session.SessionId;
            try
            {
                await child.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            finally
            {
                try
                {
                    Release(sessionId);
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }
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
