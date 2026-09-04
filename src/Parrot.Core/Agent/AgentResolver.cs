namespace Parrot.Agent;

internal sealed class AgentResolver(
    AgentIdentity owner,
    AgentSessionParentScope parentScope,
    IAgentSessionScope ownerScope,
    AgentRegistry authority)
{
    private const string ParentRecipient = "parent";

    public IAgentSessionScope ResolveStatusTargetScope(string sessionIdOrName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionIdOrName);
        _ = ownerScope.ChildRegistry.RequireOwnerScope();
        var canonical = authority.FindScope(sessionIdOrName);
        return canonical is not null && canonical.Session.Depth > 0
            ? canonical
            : ownerScope.ChildRegistry.ResolveDirectChildScope(sessionIdOrName);
    }

    public IAgentSession ResolveStatusTarget(string sessionIdOrName) =>
        ResolveStatusTargetScope(sessionIdOrName).Session;

    public IAgentSession ResolveRecipient(string sessionIdOrName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionIdOrName);
        _ = ownerScope.ChildRegistry.RequireOwnerScope();

        if (sessionIdOrName.Contains('/', StringComparison.Ordinal))
        {
            return ResolveDescendantPath(sessionIdOrName).Session;
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

        return ownerScope.ChildRegistry.ResolveDirectChildScope(sessionIdOrName).Session;
    }

    private IAgentSessionScope ResolveDescendantPath(string path)
    {
        var registry = ownerScope.ChildRegistry;
        IAgentSessionScope? descendant = null;

        foreach (var segment in path.Split('/', StringSplitOptions.None))
        {
            if (segment.Length == 0)
            {
                throw new AgentRegistryException($"child agent not found: {path}");
            }

            try
            {
                descendant = registry.ResolveNamedChildScope(segment);
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
