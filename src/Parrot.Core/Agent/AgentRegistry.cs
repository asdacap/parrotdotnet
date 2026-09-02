using System.Text;
using Parrot.Config;
using Parrot.Events;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Agent;

// Owns the child-agent identities in one user session. AgentSession owns each
// child's drain and all operations on it; _gate protects admission and names.
internal sealed class AgentRegistry(
    IAgentSessionFactory agentSessions,
    EventBroker eventBroker,
    EventRepository eventRepository,
    ProfileRegistry profiles,
    PromptTemplateCatalog promptTemplates,
    CancellationToken lifetime) : IAsyncDisposable, IActiveWorkSource, IAgentStatusSource
{
    private const string ParentRecipient = "parent";
    private const int MaxDepth = 4;
    private const int MaxRetained = 1024;
    private readonly Dictionary<string, IAgentSessionScope> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IAgentSessionScope> _scopes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentCompletionDeliveryPolicy> _completionDeliveryPolicies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _namesByParent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentSession> _parents = new(StringComparer.Ordinal);
    private readonly List<Task> _rejectedScopeDisposals = [];
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private readonly Lock _gate = new();
    private readonly Dictionary<(string ParentSessionId, string ProfileId), int> _pendingProfiles = [];

    private bool _accepting = true;
    private int _pendingSpawns;
    private TaskCompletionSource? _spawnsSettled;
    private RuntimeStatus? _status;
    private Task? _shutdown;

    public void AttachStatus(RuntimeStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        lock (_gate)
        {
            _status = _status is null
                ? status
                : throw new AgentRegistryException("the runtime status is already attached");
        }
    }

    public AgentSession Spawn(AgentLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Parent);
        ArgumentNullException.ThrowIfNull(request.Selection);
        ArgumentNullException.ThrowIfNull(request.RequestedScope);
        var profile = profiles.ResolveChild(request.RequestedProfile);
        RuntimeStatus status;
        AgentIdentity identity;
        AgentSessionParentScope parentScope;
        Security.SecurityProfile securityProfile;

        lock (_gate)
        {
            if (!_accepting)
            {
                throw new AgentRegistryException("the user session is shutting down");
            }

            if (_entries.Count + _pendingSpawns >= MaxRetained)
            {
                throw new AgentRegistryException("subagent retention limit reached");
            }

            var depth = request.Parent.Depth + 1;

            if (depth > MaxDepth)
            {
                throw new AgentRegistryException("subagent depth limit reached");
            }

            securityProfile = ResolveSecurityProfile(request.Parent).RestrictWith(profile.SecurityProfile);
            if (ProfileOccurrences(request.Parent, profile.Id)
                + _pendingProfiles.GetValueOrDefault((request.Parent.SessionId, profile.Id))
                >= profile.RecursionLimit)
            {
                throw new AgentRegistryException("subagent profile recursion limit reached");
            }

            if (!_scopes.TryGetValue(request.Parent.SessionId, out var registeredParentScope)
                || !ReferenceEquals(registeredParentScope.Session, request.Parent))
            {
                throw new AgentRegistryException($"parent agent scope not found: {request.Parent.SessionId}");
            }

            parentScope = AgentSessionParentScope.Child(registeredParentScope);
            status = _status
                ?? throw new AgentRegistryException("the runtime status is not attached");
            var sessionId = Identifier.AgentSession();
            var names = NamesFor(request.Parent.SessionId);
            var name = UniqueName(names, request.RequestedName, sessionId);
            var agentScope = request.Parent.ResolveScope().DeriveChild(name, depth, request.RequestedScope);
            identity = AgentIdentity.Child(sessionId, request.Parent.SessionId, request.Parent.Name, name, depth, agentScope, promptTemplates);
            _parents[request.Parent.SessionId] = request.Parent;
            names.Add(name, sessionId);
            _pendingSpawns++;
            var pendingProfile = (request.Parent.SessionId, profile.Id);
            _pendingProfiles[pendingProfile] = _pendingProfiles.GetValueOrDefault(pendingProfile) + 1;
        }

        var historyInitialized = false;
        IAgentSessionScope? createdScope = null;
        try
        {
            eventRepository.InitializeForkedAgentHistory(
                request.Parent.SessionId,
                identity.SessionId,
                request.AssistantSequence,
                request.SpawnToolCallId,
                request.Fork);
            historyInitialized = true;
            createdScope = agentSessions.Create(
                identity,
                parentScope,
                request.Model,
                eventBroker,
                eventRepository,
                new NoopMode(profile, securityProfile),
                securityProfile,
                status,
                this,
                _lifetime.Token);
            RegisterSpawnedScope(createdScope, request.DeliveryPolicy);
            return createdScope.Session;
        }
        catch
        {
            if (createdScope?.DisposeAsync().AsTask() is { } rejectedScopeDisposal)
            {
                lock (_gate)
                {
                    _rejectedScopeDisposals.Add(rejectedScopeDisposal);
                }
            }

            if (historyInitialized)
            {
                eventRepository.CleanupForkedAgentHistory(identity.SessionId);
            }

            throw;
        }
        finally
        {
            CompleteSpawn(identity, profile.Id);
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
            if (!_accepting)
            {
                throw new AgentRegistryException("the user session is shutting down");
            }

            RegisterScope(scope);
        }
    }

    public void UnregisterRootScope(IAgentSessionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        lock (_gate)
        {
            if (!_scopes.TryGetValue(scope.Session.SessionId, out var registered)
                || !ReferenceEquals(registered, scope)
                || scope.Session.Depth != 0)
            {
                throw new AgentRegistryException($"agent scope not found: {scope.Session.SessionId}");
            }

            _ = _scopes.Remove(scope.Session.SessionId);
        }
    }

    public AgentSession GetChild(AgentSession caller, string sessionIdOrName)
    {
        ArgumentNullException.ThrowIfNull(caller);

        lock (_gate)
        {
            return ResolveChild(caller.SessionId, sessionIdOrName);
        }
    }

    public AgentSession AuthorizeDirectChild(string parentSessionId, string childSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(childSessionId);

        lock (_gate)
        {
            if (_entries.TryGetValue(childSessionId, out var child)
                && string.Equals(child.Session.ParentSessionId, parentSessionId, StringComparison.Ordinal))
            {
                return child.Session;
            }

            throw new AgentRegistryException($"child agent not found: {childSessionId}");
        }
    }

    public AgentSession AuthorizeDirectParent(AgentSession child)
    {
        ArgumentNullException.ThrowIfNull(child);

        lock (_gate)
        {
            if (_entries.TryGetValue(child.SessionId, out var registered)
                && ReferenceEquals(registered.Session, child)
                && _parents.TryGetValue(child.ParentSessionId, out var parent))
            {
                return parent;
            }

            throw new AgentRegistryException($"parent agent not found: {child.ParentSessionId}");
        }
    }

    public AgentSession GetRecipient(AgentSession sender, string sessionIdOrName)
    {
        ArgumentNullException.ThrowIfNull(sender);

        lock (_gate)
        {
            if (sessionIdOrName.Contains('/', StringComparison.Ordinal))
            {
                return ResolveDescendantPath(sender.SessionId, sessionIdOrName);
            }

            if (_entries.TryGetValue(sessionIdOrName, out var canonical))
            {
                if (string.Equals(canonical.Session.SessionId, sender.ParentSessionId, StringComparison.Ordinal)
                    || string.Equals(canonical.Session.ParentSessionId, sender.SessionId, StringComparison.Ordinal))
                {
                    return canonical.Session;
                }

                throw new AgentRegistryException("only parent/child may be sent");
            }

            if (_parents.TryGetValue(sender.ParentSessionId, out var parent)
                && (string.Equals(sessionIdOrName, ParentRecipient, StringComparison.Ordinal)
                    || string.Equals(sessionIdOrName, sender.ParentSessionId, StringComparison.Ordinal)
                    || string.Equals(sessionIdOrName, sender.ParentSessionName, StringComparison.Ordinal)))
            {
                return parent;
            }

            return ResolveChild(sender.SessionId, sessionIdOrName);
        }
    }

    public AgentSelection ResolveSelection(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            var selected = session.Selection();
            return selected.Profile is null
                ? selected
                : selected with { SecurityProfile = ResolveSecurityProfile(session) };
        }
    }

    public IReadOnlyList<ActiveWorkObservation> Active()
    {
        lock (_gate)
        {
            return ObserveActiveChildren(null);
        }
    }

    public IReadOnlyList<ActiveAgentSnapshot> ActiveSnapshot()
    {
        lock (_gate)
        {
            var snapshots = _entries.Values
                .Select(static entry => entry.Session)
                .Where(static agent => agent.IsActive())
                .Select(static agent => new ActiveAgentSnapshot(
                    agent.SessionId,
                    agent.ParentSessionId,
                    agent.Name))
                .OrderBy(static snapshot => snapshot.SessionId, StringComparer.Ordinal)
                .ToArray();

            return Array.AsReadOnly(snapshots);
        }
    }

    public IReadOnlyList<ActiveWorkObservation> ActiveDirectChildren(string parentSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentSessionId);

        lock (_gate)
        {
            return ObserveActiveChildren(parentSessionId);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_shutdown is null)
            {
                _accepting = false;
                _shutdown = Shutdown();
            }

            return new ValueTask(_shutdown);
        }
    }

    internal async Task Deliver(AgentIdentity child, AgentExecution completed)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(completed);

        AgentSession? parent;

        lock (_gate)
        {
            parent = _accepting
                && child.ParentSessionId.Length > 0
                && _completionDeliveryPolicies.GetValueOrDefault(child.SessionId) == AgentCompletionDeliveryPolicy.Automatic
                ? _parents.GetValueOrDefault(child.ParentSessionId)
                : null;
        }

        if (parent is null)
        {
            return;
        }

        try
        {
            await parent.ReceiveAgentCompletion(child.Name, completed.FormatCompletion(child, promptTemplates), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The child has already reached its terminal boundary. A parent
            // that disappears while admitting this best-effort notice cannot
            // rewrite that retained result.
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

    private static string UniqueName(Dictionary<string, string> names, string requestedName, string sessionId)
    {
        var basis = Sanitize(requestedName);

        if (basis.Length == 0)
        {
            basis = $"agent-{sessionId[^6..]}";
        }

        var candidate = basis;
        var suffix = 2;

        while (names.ContainsKey(candidate))
        {
            candidate = $"{basis}-{suffix}";
            suffix++;
        }

        return candidate;
    }

    private IReadOnlyList<ActiveWorkObservation> ObserveActiveChildren(string? parentSessionId) =>
        [.. _entries.Values
            .Select(static entry => entry.Session)
            .Where(agent => agent.IsActive()
                && (parentSessionId is null
                    || string.Equals(agent.ParentSessionId, parentSessionId, StringComparison.Ordinal)))
            .Select(agent => new ActiveWorkObservation(
                agent.SessionId,
                agent.Name,
                ActiveWorkKind.Agent,
                ActiveWorkState.Running))
            .OrderBy(item => item.Id, StringComparer.Ordinal)];

    private Security.SecurityProfile ResolveSecurityProfile(AgentSession session)
    {
        var selected = session.Selection();
        if (selected.Profile is null || !_parents.TryGetValue(session.ParentSessionId, out var parent))
        {
            return selected.SecurityProfile;
        }

        return ResolveSecurityProfile(parent).RestrictWith(selected.Profile.SecurityProfile);
    }

    private int ProfileOccurrences(AgentSession parent, string profileId)
    {
        var occurrences = 0;
        var current = parent;

        while (current is not null)
        {
            if (string.Equals(current.Selection().Profile.Id, profileId, StringComparison.Ordinal))
            {
                occurrences++;
            }

            current = _entries.GetValueOrDefault(current.ParentSessionId)?.Session;
        }

        return occurrences;
    }

    private void RegisterSpawnedScope(IAgentSessionScope scope, AgentCompletionDeliveryPolicy deliveryPolicy)
    {
        lock (_gate)
        {
            if (!_accepting)
            {
                throw new AgentRegistryException("the user session is shutting down");
            }

            var session = scope.Session;
            if (session.ParentSessionId.Length == 0)
            {
                throw new AgentRegistryException("only child agent scopes may be registered through spawn");
            }

            RegisterScope(scope);
            try
            {
                _entries.Add(session.SessionId, scope);
                _completionDeliveryPolicies.Add(session.SessionId, deliveryPolicy);
            }
            catch
            {
                _ = _scopes.Remove(session.SessionId);
                _ = _entries.Remove(session.SessionId);
                _ = _completionDeliveryPolicies.Remove(session.SessionId);
                throw;
            }
        }
    }

    private void RegisterScope(IAgentSessionScope scope)
    {
        var sessionId = scope.Session.SessionId;
        if (_scopes.ContainsKey(sessionId)
            || _scopes.Values.Any(registered => ReferenceEquals(registered.Session, scope.Session)))
        {
            throw new AgentRegistryException($"agent scope identity is already registered: {sessionId}");
        }

        _scopes.Add(sessionId, scope);
    }

    private void CompleteSpawn(AgentIdentity identity, string profileId)
    {
        TaskCompletionSource? settled = null;
        lock (_gate)
        {
            _pendingSpawns--;
            var pendingProfile = (identity.ParentSessionId, profileId);
            if (_pendingProfiles.GetValueOrDefault(pendingProfile) == 1)
            {
                _ = _pendingProfiles.Remove(pendingProfile);
            }
            else
            {
                _pendingProfiles[pendingProfile]--;
            }

            if (!_entries.ContainsKey(identity.SessionId))
            {
                var names = _namesByParent.GetValueOrDefault(identity.ParentSessionId);
                if (names?.GetValueOrDefault(identity.Name) == identity.SessionId)
                {
                    _ = names.Remove(identity.Name);
                }
            }

            if (_pendingSpawns == 0)
            {
                settled = _spawnsSettled;
                _spawnsSettled = null;
            }
        }

        _ = settled?.TrySetResult();
    }

    private async Task Shutdown()
    {
        await Task.Yield();
        Task pendingSpawns;
        lock (_gate)
        {
            if (_pendingSpawns == 0)
            {
                pendingSpawns = Task.CompletedTask;
            }
            else
            {
                _spawnsSettled ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                pendingSpawns = _spawnsSettled.Task;
            }
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        await pendingSpawns.ConfigureAwait(false);
        IAgentSessionScope[] children;
        Task[] rejectedScopeDisposals;
        lock (_gate)
        {
            children = [.. _entries.Values];
            rejectedScopeDisposals = [.. _rejectedScopeDisposals];
        }

        await Task.WhenAll(rejectedScopeDisposals).ConfigureAwait(false);
        await Task.WhenAll(children.Select(child => child.Session.Settled())).ConfigureAwait(false);
        for (var index = children.Length - 1; index >= 0; index--)
        {
            await children[index].DisposeAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _ = _scopes.Remove(children[index].Session.SessionId);
            }
        }

        _lifetime.Dispose();
    }

    private Dictionary<string, string> NamesFor(string parentSessionId)
    {
        if (!_namesByParent.TryGetValue(parentSessionId, out var names))
        {
            names = new Dictionary<string, string>(StringComparer.Ordinal);
            _namesByParent.Add(parentSessionId, names);
        }

        return names;
    }

    private AgentSession ResolveDescendantPath(string senderSessionId, string path)
    {
        var currentSessionId = senderSessionId;
        AgentSession? descendant = null;

        foreach (var segment in path.Split('/', StringSplitOptions.None))
        {
            if (segment.Length == 0
                || !_namesByParent.TryGetValue(currentSessionId, out var names)
                || !names.TryGetValue(segment, out var sessionId)
                || !_entries.TryGetValue(sessionId, out var entry))
            {
                throw new AgentRegistryException($"child agent not found: {path}");
            }

            descendant = entry.Session;
            currentSessionId = sessionId;
        }

        return descendant ?? throw new AgentRegistryException($"child agent not found: {path}");
    }

    private AgentSession ResolveChild(string callerSessionId, string sessionIdOrName)
    {
        if (_entries.TryGetValue(sessionIdOrName, out var canonical))
        {
            return canonical.Session;
        }

        var sessionId = _namesByParent.GetValueOrDefault(callerSessionId)?.GetValueOrDefault(sessionIdOrName);

        if (sessionId is null || !_entries.TryGetValue(sessionId, out var child))
        {
            throw new AgentRegistryException($"child agent not found: {sessionIdOrName}");
        }

        return child.Session;
    }
}
