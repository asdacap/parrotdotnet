using Parrot.Questions;

namespace Parrot.Agent;

internal sealed class AgentSessionParentScope(
    AgentIdentity? owner,
    IAgentRegistry? registry,
    Func<IAgentSessionScope>? ownerScopeAccessor,
    IChildRegistry? childRegistry,
    IAgentSessionScope? parent,
    AgentCompletionDeliveryPolicy deliveryPolicy)
{
    public bool HasParent => Parent is not null;

    internal IAgentSessionScope? Parent { get; } = parent;

    internal AgentCompletionDeliveryPolicy DeliveryPolicy { get; } = deliveryPolicy;

    internal ChildQuestionCoordinator ChildQuestions => Parent?.ChildQuestions
        ?? throw new AgentRegistryException("child agent identity requires a parent scope");

    internal AgentPolicyLineage PolicyLineage => Parent is { } immediateParent
        ? immediateParent.Session.ResolvePolicyLineage().Link(immediateParent.Session)
        : AgentPolicyLineage.Root();

    internal string OwnerSessionId => owner?.SessionId
        ?? throw new InvalidOperationException("The parent scope is not bound to an agent scope.");

    public static AgentSessionParentScope Root() =>
        new(null, null, null, null, null, AgentCompletionDeliveryPolicy.RetainedOnly);

    public static AgentSessionParentScope Child(
        IAgentSessionScope parent,
        AgentCompletionDeliveryPolicy deliveryPolicy)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return new(null, null, null, null, parent, deliveryPolicy);
    }

    public static AgentSessionParentScope Bind(
        AgentIdentity owner,
        IAgentRegistry registry,
        Func<IAgentSessionScope> ownerScopeAccessor,
        IChildRegistry children,
        AgentSessionParentLink link)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(ownerScopeAccessor);
        ArgumentNullException.ThrowIfNull(children);
        ArgumentNullException.ThrowIfNull(link);
        return new(owner, registry, ownerScopeAccessor, children, link.Parent, link.DeliveryPolicy);
    }

    public void Validate(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (identity.Depth == 0)
        {
            if (identity.ParentSessionId.Length != 0)
            {
                throw new AgentRegistryException("root agent identity cannot have a parent session");
            }

            if (HasParent)
            {
                throw new AgentRegistryException("root agent identity cannot have a parent scope");
            }

            return;
        }

        if (identity.ParentSessionId.Length == 0)
        {
            throw new AgentRegistryException("child agent identity requires a parent session");
        }

        if (Parent is not { } immediateParent)
        {
            throw new AgentRegistryException("child agent identity requires a parent scope");
        }

        if (!string.Equals(immediateParent.Session.SessionId, identity.ParentSessionId, StringComparison.Ordinal))
        {
            throw new AgentRegistryException(
                $"agent parent scope does not match parent session: expected {identity.ParentSessionId}, actual {immediateParent.Session.SessionId}");
        }
    }

    internal IAgentSessionScope AuthorizeDirectChild(string childSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childSessionId);
        _ = RequireOwnerScope();
        var childScope = RequireChildRegistry().FindDirectChildScope(childSessionId);
        return childScope
            ?? throw new AgentRegistryException($"child agent not found: {childSessionId}");
    }

    internal IAgentSessionScope RequireOwnerScope()
    {
        var authority = registry
            ?? throw new InvalidOperationException("The parent scope is not bound to an agent scope.");
        if (!authority.IsAccepting)
        {
            throw new AgentRegistryException("the user session is shutting down");
        }

        var childRegistry = RequireChildRegistry();
        if (!childRegistry.IsAccepting)
        {
            throw new AgentRegistryException("the user session is shutting down");
        }

        var ownerScope = ownerScopeAccessor?.Invoke()
            ?? throw new InvalidOperationException("The parent scope is not bound to an agent scope.");
        return authority.ContainsScope(ownerScope)
            ? ownerScope
            : throw new AgentRegistryException($"parent agent scope not found: {OwnerSessionId}");
    }

    internal void ValidateOwnerScope(IAgentSessionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var ownerScope = ownerScopeAccessor?.Invoke()
            ?? throw new InvalidOperationException("The parent scope is not bound to an agent scope.");
        if (!ReferenceEquals(ownerScope, scope)
            || !ReferenceEquals(scope.ChildRegistry, childRegistry))
        {
            throw new AgentRegistryException($"agent scope does not match child registry owner: {OwnerSessionId}");
        }
    }

    private IChildRegistry RequireChildRegistry() =>
        childRegistry ?? throw new InvalidOperationException("The parent scope is not bound to an agent scope.");
}
