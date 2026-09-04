using Parrot.Questions;

namespace Parrot.Agent;

internal sealed class AgentSessionParentScope
{
    private AgentSessionParentScope(
        IAgentSessionScope? parent,
        AgentCompletionDeliveryPolicy deliveryPolicy)
    {
        Parent = parent;
        DeliveryPolicy = deliveryPolicy;
    }

    public bool HasParent => Parent is not null;

    internal AgentCompletionDeliveryPolicy DeliveryPolicy { get; }

    internal ChildQuestionCoordinator ChildQuestions => Parent?.ChildQuestions
        ?? throw new AgentRegistryException("child agent identity requires a parent scope");

    internal AgentPolicyLineage PolicyLineage => Parent is { } parent
        ? parent.Session.ResolvePolicyLineage().Link(parent.Session)
        : AgentPolicyLineage.Root();

    internal IAgentSessionScope? Parent { get; }

    public static AgentSessionParentScope Root() =>
        new(null, AgentCompletionDeliveryPolicy.RetainedOnly);

    public static AgentSessionParentScope Child(
        IAgentSessionScope parent,
        AgentCompletionDeliveryPolicy deliveryPolicy)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return new(parent, deliveryPolicy);
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

        if (Parent is not { } parent)
        {
            throw new AgentRegistryException("child agent identity requires a parent scope");
        }

        if (!string.Equals(parent.Session.SessionId, identity.ParentSessionId, StringComparison.Ordinal))
        {
            throw new AgentRegistryException(
                $"agent parent scope does not match parent session: expected {identity.ParentSessionId}, actual {parent.Session.SessionId}");
        }
    }
}
