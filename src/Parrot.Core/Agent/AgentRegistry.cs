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
    CancellationToken lifetime) : IAsyncDisposable, IActiveWorkSource
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
        string requestedName)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(selection);
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

            if (!selection.SecurityProfile.AllowsDelegationTo(profile.SecurityProfile))
            {
                throw new AgentRegistryException("the selected child profile exceeds the caller's security policy");
            }

            if (ProfileOccurrences(parent, profile.Id) >= profile.RecursionLimit)
            {
                throw new AgentRegistryException("subagent profile recursion limit reached");
            }

            var sessionId = Identifier.AgentSession();
            var names = NamesFor(parent.SessionId);
            var name = UniqueName(names, requestedName, sessionId);
            var identity = AgentIdentity.Child(sessionId, parent.SessionId, parent.Name, name, depth);
            var lease = agentSessions.Create(
                identity,
                model,
                eventBroker,
                eventRepository,
                profile,
                profile.SecurityProfile.WithoutRuntimeCapabilities(),
                _status,
                this,
                _lifetime.Token);

            _entries.Add(sessionId, lease);
            names.Add(name, sessionId);
            return lease.Session;
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
                return canonical.Session;
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

    public IReadOnlyList<ActiveWorkObservation> Active()
    {
        lock (_gate)
        {
            return [.. _entries.Values
                .Select(static entry => entry.Session)
                .Where(agent => agent.State != DrainState.Idle)
                .Select(agent => new ActiveWorkObservation(
                    agent.SessionId,
                    agent.Name,
                    ActiveWorkKind.Agent,
                    ActiveWorkState.Running))
                .OrderBy(item => item.Id, StringComparer.Ordinal)];
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

    private int ProfileOccurrences(AgentSession parent, string profileId)
    {
        var occurrences = 0;
        var current = parent;

        while (current is not null)
        {
            if (string.Equals(current.Selection().Profile?.Id, profileId, StringComparison.Ordinal))
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

        foreach (var child in children)
        {
            await child.DisposeAsync().ConfigureAwait(false);
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
