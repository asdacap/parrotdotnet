namespace Parrot.Agent;

internal sealed class AgentResolver(
    AgentIdentity owner,
    IAgentParentScope parentScope,
    IAgentSessionScope ownerScope,
    IAgentRegistry authority) : IAgentResolver
{
    private const string ParentRecipient = "parent";

    public IAgentSessionScope ResolveStatusTargetScope(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        RequireRegisteredOwner();
        return ownerScope.ChildRegistry.ResolveNamedChildScope(name);
    }

    public IAgentSession ResolveStatusTarget(string name) =>
        ResolveStatusTargetScope(name).Session;

    public IAgentSession ResolveRecipient(string nameOrPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrPath);
        RequireRegisteredOwner();

        if (nameOrPath.Contains('/', StringComparison.Ordinal))
        {
            return ResolveDescendantPath(nameOrPath).Session;
        }

        if (parentScope.Parent is { } parent
            && (string.Equals(nameOrPath, ParentRecipient, StringComparison.Ordinal)
                || string.Equals(nameOrPath, owner.ParentSessionName, StringComparison.Ordinal)))
        {
            return parent.Session;
        }

        return ownerScope.ChildRegistry.ResolveNamedChildScope(nameOrPath).Session;
    }

    private void RequireRegisteredOwner()
    {
        if (!authority.IsAccepting)
        {
            throw new AgentRegistryException("the user session is shutting down");
        }

        if (!authority.ContainsScope(ownerScope))
        {
            throw new AgentRegistryException($"parent agent scope not found: {owner.SessionId}");
        }
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
