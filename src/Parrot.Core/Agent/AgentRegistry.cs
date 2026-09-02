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
    private readonly Dictionary<string, IAgentSessionLease> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentCompletionDeliveryPolicy> _completionDeliveryPolicies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, string>> _namesByParent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentSession> _parents = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private readonly Lock _gate = new();

    private bool _accepting = true;
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

        lock (_gate)
        {
            if (!_accepting)
            {
                throw new AgentRegistryException("the user session is shutting down");
            }

            if (_entries.Count >= MaxRetained)
            {
                throw new AgentRegistryException("subagent retention limit reached");
            }

            var depth = request.Parent.Depth + 1;

            if (depth > MaxDepth)
            {
                throw new AgentRegistryException("subagent depth limit reached");
            }

            _parents[request.Parent.SessionId] = request.Parent;
            var securityProfile = ResolveSecurityProfile(request.Parent).RestrictWith(profile.SecurityProfile);
            var mode = new NoopMode(profile, securityProfile);

            if (ProfileOccurrences(request.Parent, profile.Id) >= profile.RecursionLimit)
            {
                throw new AgentRegistryException("subagent profile recursion limit reached");
            }

            var status = _status
                ?? throw new AgentRegistryException("the runtime status is not attached");
            var sessionId = Identifier.AgentSession();
            var names = NamesFor(request.Parent.SessionId);
            var name = UniqueName(names, request.RequestedName, sessionId);
            var scope = request.Parent.ResolveScope().DeriveChild(name, depth, request.RequestedScope);
            var identity = AgentIdentity.Child(sessionId, request.Parent.SessionId, request.Parent.Name, name, depth, scope, promptTemplates);
            eventRepository.InitializeForkedAgentHistory(
                request.Parent.SessionId,
                sessionId,
                request.AssistantSequence,
                request.SpawnToolCallId,
                request.Fork);
            try
            {
                var lease = agentSessions.Create(
                    identity,
                    request.Model,
                    eventBroker,
                    eventRepository,
                    mode,
                    securityProfile,
                    status,
                    this,
                    _lifetime.Token);

                _entries.Add(sessionId, lease);
                _completionDeliveryPolicies.Add(sessionId, request.DeliveryPolicy);
                names.Add(name, sessionId);
                return lease.Session;
            }
            catch
            {
                eventRepository.CleanupForkedAgentHistory(sessionId);
                throw;
            }
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
                _shutdown = Shutdown([.. _entries.Values]);
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

    private async Task Shutdown(IAgentSessionLease[] children)
    {
        await Task.Yield();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(children.Select(child => child.Session.Settled())).ConfigureAwait(false);

        for (var index = children.Length - 1; index >= 0; index--)
        {
            await children[index].DisposeAsync().ConfigureAwait(false);
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
