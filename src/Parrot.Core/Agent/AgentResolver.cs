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
        return name.Contains('/', StringComparison.Ordinal)
            ? ResolvePath(name)
            : ownerScope.ChildRegistry.ResolveNamedChildScope(name);
    }

    public IAgentSession ResolveStatusTarget(string name) =>
        ResolveStatusTargetScope(name).Session;

    public IAgentSession ResolveRecipient(string nameOrPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrPath);
        RequireRegisteredOwner();

        if (nameOrPath.Contains('/', StringComparison.Ordinal))
        {
            return ResolvePath(nameOrPath).Session;
        }

        if (parentScope.Parent is { } parent
            && (string.Equals(nameOrPath, ParentRecipient, StringComparison.Ordinal)
                || string.Equals(nameOrPath, owner.ParentSessionName, StringComparison.Ordinal)))
        {
            return parent.Session;
        }

        return ownerScope.ChildRegistry.ResolveNamedChildScope(nameOrPath).Session;
    }

    public (IAgentSessionScope Scope, string Name) ResolveResource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        RequireRegisteredOwner();
        var separator = path.LastIndexOf('/');
        if (separator <= 0 || separator == path.Length - 1)
        {
            throw new AgentRegistryException($"invalid resource path: {path}");
        }

        var scope = ResolvePath(path[..separator]);
        if (!ownerScope.Session.ResolvePolicySelection().SecurityProfile.AllowsDelegationTo(
                scope.Session.ResolvePolicySelection().SecurityProfile))
        {
            throw new AgentRegistryException("cannot access resources of a more permissive agent");
        }

        return (scope, path[(separator + 1)..]);
    }

    public bool IsAncestor(IAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        for (var scope = parentScope.Parent; scope is not null; scope = scope.ParentScope.Parent)
        {
            if (ReferenceEquals(scope.Session, session))
            {
                return true;
            }
        }

        return false;
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

    private IAgentSessionScope ResolvePath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.None);
        var scope = ownerScope;
        var start = 0;
        if (path.StartsWith('/'))
        {
            var roots = authority.SnapshotScopes()
                .Where(candidate => !candidate.ParentScope.HasParent
                    && string.Equals(candidate.Session.Name, segments[1], StringComparison.Ordinal))
                .ToArray();
            if (roots.Length != 1)
            {
                throw new AgentRegistryException($"child agent not found: {path}");
            }

            scope = roots[0];
            start = 2;
        }

        for (var index = start; index < segments.Length; index++)
        {
            var segment = segments[index];
            if (string.IsNullOrWhiteSpace(segment) || segment is "." or "..")
            {
                throw new AgentRegistryException($"child agent not found: {path}");
            }

            try
            {
                scope = string.Equals(segment, ParentRecipient, StringComparison.Ordinal)
                    ? scope.ParentScope.Parent ?? throw new AgentRegistryException($"child agent not found: {path}")
                    : scope.ChildRegistry.ResolveNamedChildScope(segment);
            }
            catch (AgentRegistryException)
            {
                throw new AgentRegistryException($"child agent not found: {path}");
            }
        }

        return scope;
    }
}
