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
    CancellationToken lifetime) : IAsyncDisposable, IActiveWorkSource
{
    private const int MaxDepth = 4;
    private const int MaxRetained = 1024;
    private readonly Dictionary<string, IAgentSessionLease> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
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
        Llm.ModelSelector model,
        string requestedName)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(selection);

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

            var sessionId = Identifier.AgentSession();
            var name = UniqueName(requestedName, sessionId);
            var childSecurityProfile = selection.SecurityProfile.WithoutRuntimeCapabilities();
            var identity = AgentIdentity.Child(sessionId, parent.SessionId, parent.Name, name, depth);
            var lease = agentSessions.Create(
                identity,
                model,
                eventBroker,
                eventRepository,
                selection.Profile?.ForChild(childSecurityProfile),
                childSecurityProfile,
                _status,
                _lifetime.Token);

            _entries.Add(sessionId, lease);
            _names.Add(name, sessionId);
            return lease.Session;
        }
    }

    public AgentSession Get(string sessionIdOrName)
    {
        lock (_gate)
        {
            return Resolve(sessionIdOrName);
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

    private AgentSession Resolve(string sessionIdOrName)
    {
        var sessionId = _entries.ContainsKey(sessionIdOrName)
            ? sessionIdOrName
            : _names.GetValueOrDefault(sessionIdOrName);

        if (sessionId is null || !_entries.TryGetValue(sessionId, out var child))
        {
            throw new AgentRegistryException($"child agent not found: {sessionIdOrName}");
        }

        return child.Session;
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
}
