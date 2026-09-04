using System.Runtime.ExceptionServices;
using System.Text;

namespace Parrot.Agent;

internal sealed class ChildRegistry(
    AgentIdentity owner,
    IAgentRegistry authority,
    Func<IAgentSessionScope> ownerScopeAccessor) : IChildRegistry
{
    private const int MaxDepth = 4;
    private readonly Dictionary<string, ChildEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pendingProfiles = new(StringComparer.Ordinal);
    private readonly List<Task> _rejectedScopeDisposals = [];
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(authority.ChildLifetime);
    private readonly Lock _gate = new();
    private readonly Func<IAgentSessionScope> _ownerAccessor = ownerScopeAccessor
        ?? throw new ArgumentNullException(nameof(ownerScopeAccessor));

    private bool _accepting = true;
    private int _pendingConstructions;
    private TaskCompletionSource? _constructionsSettled;
    private Task? _shutdown;

    public string OwnerSessionId => owner.SessionId;

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

    public IAgentSessionScope SpawnScope(AgentLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Parent);
        ArgumentNullException.ThrowIfNull(request.Selection);
        ArgumentNullException.ThrowIfNull(request.RequestedScope);

        if (!ReferenceEquals(request.Parent.Identity, owner))
        {
            throw new AgentRegistryException(
                $"launch parent does not match child registry owner: expected {owner.SessionId}, actual {request.Parent.SessionId}");
        }

        var depth = owner.Depth + 1;
        if (depth > MaxDepth)
        {
            throw new AgentRegistryException("subagent depth limit reached");
        }

        var registeredOwnerScope = RequireOwnerScope();
        var profile = authority.ResolveChildProfile(request.RequestedProfile);
        var status = authority.RequireStatus();
        var retainedReservation = authority.ReserveRetainedAgent();
        AgentIdentity childIdentity;
        AgentSessionParentScope childParentScope;

        try
        {
            lock (_gate)
            {
                EnsureAccepting();
                if (!ReferenceEquals(_ownerAccessor(), registeredOwnerScope))
                {
                    throw new AgentRegistryException($"parent agent scope not found: {owner.SessionId}");
                }

                childParentScope = AgentSessionParentScope.Child(registeredOwnerScope, request.DeliveryPolicy);
                if (childParentScope.PolicyLineage.CountProfile(profile.Id)
                    + _pendingProfiles.GetValueOrDefault(profile.Id)
                    >= profile.RecursionLimit)
                {
                    throw new AgentRegistryException("subagent profile recursion limit reached");
                }

                var sessionId = Identifier.AgentSession();
                var name = UniqueName(request.RequestedName, sessionId);
                var scope = owner.Scope.DeriveChild(name, depth, request.RequestedScope);
                childIdentity = AgentIdentity.ChildWithPolicyLineage(
                    sessionId,
                    owner.SessionId,
                    owner.Name,
                    name,
                    depth,
                    scope,
                    childParentScope.PolicyLineage,
                    owner.PromptTemplates);
                _names.Add(name, sessionId);
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
            authority.InitializeChildHistory(
                owner.SessionId,
                childIdentity.SessionId,
                request.AssistantSequence,
                request.SpawnToolCallId,
                request.Fork);
            historyInitialized = true;
            var securityProfile = childParentScope.PolicyLineage.Resolve(profile.SecurityProfile);
            constructedScope = authority.CreateChildScope(
                childIdentity,
                childParentScope,
                request.Model,
                new NoopMode(profile, securityProfile),
                securityProfile,
                status,
                _lifetime.Token);
            RegisterConstructedScope(constructedScope, retainedReservation);
            return constructedScope;
        }
        catch
        {
            retainedReservation.Rollback();
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
                authority.CleanupChildHistory(childIdentity.SessionId);
            }

            throw;
        }
        finally
        {
            CompleteConstruction(childIdentity, profile.Id);
        }
    }

    public IAgentSessionScope AuthorizeDirectChild(string childSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childSessionId);
        _ = RequireOwnerScope();

        lock (_gate)
        {
            if (_accepting && _entries.TryGetValue(childSessionId, out var child))
            {
                return child.Scope;
            }
        }

        throw new AgentRegistryException($"child agent not found: {childSessionId}");
    }

    public IAgentSession AuthorizeQuestionChild(IAgentSession child)
    {
        ArgumentNullException.ThrowIfNull(child);
        var registeredChild = AuthorizeDirectChild(child.SessionId);
        if (!ReferenceEquals(registeredChild.Session, child))
        {
            throw new AgentRegistryException($"parent agent not found: {child.ParentSessionId}");
        }

        return RequireOwnerScope().Session;
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

    public void ValidateOwner(AgentIdentity identity)
    {
        if (!ReferenceEquals(identity, owner))
        {
            throw new AgentRegistryException(
                $"child registry owner does not match agent identity: expected {owner.SessionId}, actual {identity.SessionId}");
        }
    }

    public void ValidateOwnerScope(IAgentSessionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!ReferenceEquals(_ownerAccessor(), scope)
            || !ReferenceEquals(scope.ChildRegistry, this))
        {
            throw new AgentRegistryException($"agent scope does not match child registry owner: {owner.SessionId}");
        }
    }

    public IAgentSessionScope ResolveDirectChildScope(string sessionIdOrName)
    {
        lock (_gate)
        {
            if (_accepting && _entries.TryGetValue(sessionIdOrName, out var canonical))
            {
                return canonical.Scope;
            }

            if (_accepting
                && _names.TryGetValue(sessionIdOrName, out var sessionId)
                && _entries.TryGetValue(sessionId, out var named))
            {
                return named.Scope;
            }
        }

        throw new AgentRegistryException($"child agent not found: {sessionIdOrName}");
    }

    public IAgentSessionScope? FindDescendantScope(string sessionId)
    {
        foreach (var child in SnapshotChildScopes())
        {
            if (string.Equals(child.Session.SessionId, sessionId, StringComparison.Ordinal))
            {
                return child;
            }

            var descendant = child.ChildRegistry.FindDescendantScope(sessionId);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    public bool ContainsDescendantScope(IAgentSessionScope candidate)
    {
        foreach (var child in SnapshotChildScopes())
        {
            if (ReferenceEquals(child, candidate) || child.ChildRegistry.ContainsDescendantScope(candidate))
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<IAgentSession> SnapshotDescendants()
    {
        var children = SnapshotChildScopes();
        return [.. children.SelectMany(static child =>
            child.ChildRegistry.SnapshotDescendants().Prepend(child.Session))];
    }

    public IAgentSessionScope ResolveNamedChildScope(string name)
    {
        lock (_gate)
        {
            if (_accepting
                && _names.TryGetValue(name, out var sessionId)
                && _entries.TryGetValue(sessionId, out var child))
            {
                return child.Scope;
            }
        }

        throw new AgentRegistryException($"child agent not found: {name}");
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
        if (!authority.IsAccepting)
        {
            throw new AgentRegistryException("the user session is shutting down");
        }

        IAgentSessionScope scope;
        lock (_gate)
        {
            EnsureAccepting();
            scope = _ownerAccessor();
        }

        return authority.ContainsScope(scope)
            ? scope
            : throw new AgentRegistryException($"parent agent scope not found: {owner.SessionId}");
    }

    private string UniqueName(string requestedName, string sessionId)
    {
        var basis = Sanitize(requestedName);
        if (basis.Length == 0)
        {
            basis = $"agent-{sessionId[^6..]}";
        }

        var candidate = basis;
        var suffix = 2;
        while (_names.ContainsKey(candidate))
        {
            candidate = $"{basis}-{suffix}";
            suffix++;
        }

        return candidate;
    }

    private void RegisterConstructedScope(
        IAgentSessionScope scope,
        RetainedAgentReservation retainedReservation)
    {
        scope.ChildRegistry.ValidateOwner(scope.Session.Identity);
        scope.ChildRegistry.ValidateOwnerScope(scope);

        lock (_gate)
        {
            EnsureAccepting();
            if (!string.Equals(scope.Session.ParentSessionId, owner.SessionId, StringComparison.Ordinal))
            {
                throw new AgentRegistryException($"parent agent scope not found: {owner.SessionId}");
            }

            _entries.Add(scope.Session.SessionId, new ChildEntry(scope, retainedReservation));
            retainedReservation.Commit();
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

            if (!_entries.ContainsKey(childIdentity.SessionId)
                && _names.GetValueOrDefault(childIdentity.Name) == childIdentity.SessionId)
            {
                _ = _names.Remove(childIdentity.Name);
            }

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
        ChildEntry[] children;
        Task[] rejectedScopeDisposals;
        lock (_gate)
        {
            children = [.. _entries.Values];
            _entries.Clear();
            _names.Clear();
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

        for (var index = children.Length - 1; index >= 0; index--)
        {
            var child = children[index];
            try
            {
                await child.Scope.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            finally
            {
                child.RetainedReservation.Release();
            }
        }

        _lifetime.Dispose();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private IAgentSessionScope[] SnapshotChildScopes()
    {
        lock (_gate)
        {
            return _accepting ? [.. _entries.Values.Select(static entry => entry.Scope)] : [];
        }
    }

    private void EnsureAccepting()
    {
        if (!_accepting || !authority.IsAccepting)
        {
            throw new AgentRegistryException("the user session is shutting down");
        }
    }

    private sealed record ChildEntry(
        IAgentSessionScope Scope,
        RetainedAgentReservation RetainedReservation);
}
