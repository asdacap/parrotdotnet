using System.Text;
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
    CancellationToken lifetime) : IAsyncDisposable, IActiveWorkSource, IAgentStatusSource
{
    private const string ParentRecipient = "parent";
    private const int MaxDepth = 4;
    private const int MaxRetained = 1024;
    private readonly Dictionary<string, IAgentSessionLease> _entries = new(StringComparer.Ordinal);
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

    public AgentSession Spawn(
        AgentSession parent,
        AgentTurnSelection selection,
        string requestedProfile,
        Llm.ModelSelector model,
        string requestedName,
        string requestedScope) =>
        Spawn(
            parent,
            selection,
            requestedProfile,
            model,
            requestedName,
            requestedScope,
            HistoryForkSelection.Parse(string.Empty),
            0,
            string.Empty);

    public AgentSession Spawn(
        AgentSession parent,
        AgentTurnSelection selection,
        string requestedProfile,
        Llm.ModelSelector model,
        string requestedName,
        string requestedScope,
        HistoryForkSelection fork,
        long assistantSequence,
        string spawnToolCallId)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(requestedScope);
        var profile = profiles.ResolveChild(requestedProfile);

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

            var depth = parent.Depth + 1;

            if (depth > MaxDepth)
            {
                throw new AgentRegistryException("subagent depth limit reached");
            }

            _parents[parent.SessionId] = parent;
            var securityProfile = ResolveSecurityProfile(parent).RestrictWith(profile.SecurityProfile);
            var mode = new NoopMode(profile, securityProfile);

            if (ProfileOccurrences(parent, profile.Id) >= profile.RecursionLimit)
            {
                throw new AgentRegistryException("subagent profile recursion limit reached");
            }

            var status = _status
                ?? throw new AgentRegistryException("the runtime status is not attached");
            var sessionId = Identifier.AgentSession();
            var names = NamesFor(parent.SessionId);
            var name = UniqueName(names, requestedName, sessionId);
            var scope = parent.ResolveScope().DeriveChild(name, depth, requestedScope);
            var identity = AgentIdentity.Child(sessionId, parent.SessionId, parent.Name, name, depth, scope);
            eventRepository.InitializeForkedAgentHistory(
                parent.SessionId,
                sessionId,
                assistantSequence,
                spawnToolCallId,
                fork);
            try
            {
                var lease = agentSessions.Create(
                    identity,
                    model,
                    eventBroker,
                    eventRepository,
                    mode,
                    securityProfile,
                    status,
                    this,
                    _lifetime.Token);

                _entries.Add(sessionId, lease);
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

    public AgentSession GetRecipient(AgentSession sender, string sessionIdOrName)
    {
        ArgumentNullException.ThrowIfNull(sender);

        lock (_gate)
        {
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
            parent = _accepting && child.ParentSessionId.Length > 0
                ? _parents.GetValueOrDefault(child.ParentSessionId)
                : null;
        }

        if (parent is null)
        {
            return;
        }

        try
        {
            await parent.ReceiveCompletion(completed.FormatCompletion(child), CancellationToken.None)
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
