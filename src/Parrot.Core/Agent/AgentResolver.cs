namespace Parrot.Agent;

internal sealed class AgentResolver(
    AgentIdentity owner,
    AgentSessionParentScope parentScope,
    ChildRegistry children,
    AgentRegistry authority)
{
    private const string ParentRecipient = "parent";

    public AgentSession ResolveStatusTarget(string sessionIdOrName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionIdOrName);
        _ = children.RequireOwnerScope();
        var canonical = authority.FindScope(sessionIdOrName);
        return canonical is not null && canonical.Session.Depth > 0
            ? canonical.Session
            : children.ResolveDirectChild(sessionIdOrName);
    }

    public AgentSession ResolveRecipient(string sessionIdOrName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionIdOrName);
        _ = children.RequireOwnerScope();

        if (sessionIdOrName.Contains('/', StringComparison.Ordinal))
        {
            return ResolveDescendantPath(sessionIdOrName);
        }

        var canonical = authority.FindScope(sessionIdOrName);
        if (canonical is not null)
        {
            if (string.Equals(canonical.Session.SessionId, owner.ParentSessionId, StringComparison.Ordinal)
                || string.Equals(canonical.Session.ParentSessionId, owner.SessionId, StringComparison.Ordinal))
            {
                return canonical.Session;
            }

            throw new AgentRegistryException("only parent/child may be sent");
        }

        if (parentScope.Parent is { } parent
            && (string.Equals(sessionIdOrName, ParentRecipient, StringComparison.Ordinal)
                || string.Equals(sessionIdOrName, owner.ParentSessionId, StringComparison.Ordinal)
                || string.Equals(sessionIdOrName, owner.ParentSessionName, StringComparison.Ordinal)))
        {
            return parent.Session;
        }

        return children.ResolveDirectChild(sessionIdOrName);
    }

    private AgentSession ResolveDescendantPath(string path)
    {
        var registry = children;
        AgentSession? descendant = null;

        foreach (var segment in path.Split('/', StringSplitOptions.None))
        {
            if (segment.Length == 0)
            {
                throw new AgentRegistryException($"child agent not found: {path}");
            }

            try
            {
                descendant = registry.ResolveNamedChild(segment);
                registry = descendant.ChildRegistry;
            }
            catch (AgentRegistryException)
            {
                throw new AgentRegistryException($"child agent not found: {path}");
            }
        }

        return descendant ?? throw new AgentRegistryException($"child agent not found: {path}");
    }
}
