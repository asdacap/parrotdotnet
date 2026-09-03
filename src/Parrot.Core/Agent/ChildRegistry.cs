using System.Runtime.ExceptionServices;
using System.Text;
using Parrot.Statuses;

namespace Parrot.Agent;

internal sealed class ChildRegistry(
    AgentIdentity owner,
    AgentRegistry authority)
{
    private const int MaxDepth = 4;
    private readonly Dictionary<string, ChildEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _pendingProfiles = new(StringComparer.Ordinal);
    private readonly List<Task> _rejectedScopeDisposals = [];
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(authority.ChildLifetime);
    private readonly Lock _gate = new();

    private IAgentSessionScope? _ownerScope;
    private bool _accepting = true;
    private int _pendingConstructions;
    private TaskCompletionSource? _constructionsSettled;
    private Task? _shutdown;

    internal string OwnerSessionId => owner.SessionId;

    internal AgentRegistry Authority => authority;

    public AgentSession Spawn(AgentLaunchRequest request)
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
                if (!ReferenceEquals(_ownerScope, registeredOwnerScope))
                {
                    throw new AgentRegistryException($"parent agent scope not found: {owner.SessionId}");
                }

                childParentScope = AgentSessionParentScope.Child(registeredOwnerScope);
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
            RegisterConstructedScope(constructedScope, request.DeliveryPolicy, retainedReservation);
            return constructedScope.Session;
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

    public AgentSession AuthorizeDirectChild(string childSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childSessionId);
        _ = RequireOwnerScope();

        lock (_gate)
        {
            if (_accepting && _entries.TryGetValue(childSessionId, out var child))
            {
                return child.Scope.Session;
            }
        }

        throw new AgentRegistryException($"child agent not found: {childSessionId}");
    }

    public AgentSession AuthorizeQuestionChild(AgentSession child)
    {
        ArgumentNullException.ThrowIfNull(child);
        var registeredChild = AuthorizeDirectChild(child.SessionId);
        if (!ReferenceEquals(registeredChild, child))
        {
            throw new AgentRegistryException($"parent agent not found: {child.ParentSessionId}");
        }

        return RequireOwnerScope().Session;
    }

    public IReadOnlyList<ActiveWorkObservation> ObserveActive()
    {
        _ = RequireOwnerScope();
        lock (_gate)
        {
            return [.. _entries.Values
                .Select(static entry => entry.Scope.Session)
                .Where(static session => session.IsActive())
                .Select(static session => new ActiveWorkObservation(
                    session.SessionId,
                    session.Name,
                    ActiveWorkKind.Agent,
                    ActiveWorkState.Running))
                .OrderBy(static observation => observation.Id, StringComparer.Ordinal)];
        }
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

    internal void ValidateOwner(AgentIdentity identity)
    {
        if (!ReferenceEquals(identity, owner))
        {
            throw new AgentRegistryException(
                $"child registry owner does not match agent identity: expected {owner.SessionId}, actual {identity.SessionId}");
        }
    }

    internal void AttachOwnerScope(IAgentSessionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!ReferenceEquals(scope.Session.Identity, owner)
            || !ReferenceEquals(scope.Session.ChildRegistry, this)
            || !ReferenceEquals(scope.ChildRegistry, this))
        {
            throw new AgentRegistryException($"agent scope does not match child registry owner: {owner.SessionId}");
        }

        lock (_gate)
        {
            if (_ownerScope is not null && !ReferenceEquals(_ownerScope, scope))
            {
                throw new AgentRegistryException($"agent scope identity is already registered: {owner.SessionId}");
            }

            _ownerScope = scope;
        }
    }

    internal void DetachOwnerScope(IAgentSessionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        lock (_gate)
        {
            if (ReferenceEquals(_ownerScope, scope))
            {
                _ownerScope = null;
            }
        }
    }

    internal IAgentSessionScope RequireOwnerScope()
    {
        if (!authority.IsAccepting)
        {
            throw new AgentRegistryException("the user session is shutting down");
        }

        IAgentSessionScope scope;
        lock (_gate)
        {
            EnsureAccepting();
            scope = _ownerScope ?? throw new AgentRegistryException($"parent agent scope not found: {owner.SessionId}");
        }

        return authority.ContainsScope(scope)
            ? scope
            : throw new AgentRegistryException($"parent agent scope not found: {owner.SessionId}");
    }

    internal AgentSession ResolveDirectChild(string sessionIdOrName)
    {
        lock (_gate)
        {
            if (_accepting && _entries.TryGetValue(sessionIdOrName, out var canonical))
            {
                return canonical.Scope.Session;
            }

            if (_accepting
                && _names.TryGetValue(sessionIdOrName, out var sessionId)
                && _entries.TryGetValue(sessionId, out var named))
            {
                return named.Scope.Session;
            }
        }

        throw new AgentRegistryException($"child agent not found: {sessionIdOrName}");
    }

    internal IAgentSessionScope? FindDescendantScope(string sessionId)
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

    internal bool ContainsDescendantScope(IAgentSessionScope candidate)
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

    internal IReadOnlyList<AgentSession> SnapshotDescendants()
    {
        var children = SnapshotChildScopes();
        return [.. children.SelectMany(static child =>
            child.ChildRegistry.SnapshotDescendants().Prepend(child.Session))];
    }

    internal async Task ReceiveCompletion(AgentIdentity child, AgentExecution completed)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(completed);

        AgentSession? parent;
        lock (_gate)
        {
            parent = _accepting
                && _entries.TryGetValue(child.SessionId, out var entry)
                && ReferenceEquals(entry.Scope.Session.Identity, child)
                && entry.DeliveryPolicy == AgentCompletionDeliveryPolicy.Automatic
                ? _ownerScope?.Session
                : null;
        }

        if (parent is null)
        {
            return;
        }

        try
        {
            await parent.ReceiveAgentCompletion(
                child.Name,
                completed.FormatCompletion(child, owner.PromptTemplates),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    internal AgentSession ResolveNamedChild(string name)
    {
        lock (_gate)
        {
            if (_accepting
                && _names.TryGetValue(name, out var sessionId)
                && _entries.TryGetValue(sessionId, out var child))
            {
                return child.Scope.Session;
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
        AgentCompletionDeliveryPolicy deliveryPolicy,
        RetainedAgentReservation retainedReservation)
    {
        scope.ChildRegistry.AttachOwnerScope(scope);
        lock (_gate)
        {
            EnsureAccepting();
            if (!string.Equals(scope.Session.ParentSessionId, owner.SessionId, StringComparison.Ordinal))
            {
                throw new AgentRegistryException($"parent agent scope not found: {owner.SessionId}");
            }

            _entries.Add(scope.Session.SessionId, new ChildEntry(scope, deliveryPolicy, retainedReservation));
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

        try
        {
            await Task.WhenAll(children.Select(static child => child.Scope.Session.Settled())).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure ??= exception;
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
        AgentCompletionDeliveryPolicy DeliveryPolicy,
        RetainedAgentReservation RetainedReservation);
}
