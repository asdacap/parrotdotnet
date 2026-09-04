namespace Parrot.Agent;

internal sealed class ChildRegistry(AgentIdentity owner) : IChildRegistry
{
    private readonly Dictionary<string, IAgentSessionScope> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private bool _accepting = true;

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

    public IAgentSessionScope? FindDirectChildScope(string childSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childSessionId);
        lock (_gate)
        {
            return _accepting && _entries.TryGetValue(childSessionId, out var child)
                ? child
                : null;
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

    public IAgentSessionScope ResolveDirectChildScope(string sessionIdOrName)
    {
        lock (_gate)
        {
            if (_accepting && _entries.TryGetValue(sessionIdOrName, out var canonical))
            {
                return canonical;
            }

            if (_accepting
                && _names.TryGetValue(sessionIdOrName, out var sessionId)
                && _entries.TryGetValue(sessionId, out var named))
            {
                return named;
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
                return child;
            }
        }

        throw new AgentRegistryException($"child agent not found: {name}");
    }

    public bool ContainsName(string name)
    {
        lock (_gate)
        {
            return _names.ContainsKey(name);
        }
    }

    public void Add(IAgentSessionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.ChildRegistry.ValidateOwner(scope.Session.Identity);

        lock (_gate)
        {
            if (!_accepting)
            {
                throw new AgentRegistryException("the user session is shutting down");
            }

            if (!string.Equals(scope.Session.ParentSessionId, owner.SessionId, StringComparison.Ordinal))
            {
                throw new AgentRegistryException($"parent agent scope not found: {owner.SessionId}");
            }

            _entries.Add(scope.Session.SessionId, scope);
            try
            {
                _names.Add(scope.Session.Name, scope.Session.SessionId);
            }
            catch
            {
                _ = _entries.Remove(scope.Session.SessionId);
                throw;
            }
        }
    }

    public IReadOnlyList<IAgentSessionScope> TakeAll()
    {
        lock (_gate)
        {
            _accepting = false;
            var children = _entries.Values.ToArray();
            _entries.Clear();
            _names.Clear();
            return children;
        }
    }

    private IAgentSessionScope[] SnapshotChildScopes()
    {
        lock (_gate)
        {
            return _accepting ? [.. _entries.Values] : [];
        }
    }
}
