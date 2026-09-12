namespace Parrot.Agent;

internal sealed record AgentSessionParentLink(
    IAgentSessionScope? Parent,
    AgentCompletionDeliveryPolicy DeliveryPolicy)
{
    public AgentPolicyLineage PolicyLineage => Parent is { } parent
        ? parent.Session.ResolvePolicyLineage().Link(parent.Session)
        : AgentPolicyLineage.Root();

    public static AgentSessionParentLink Root() =>
        new(null, AgentCompletionDeliveryPolicy.RetainedOnly);

    public static AgentSessionParentLink Child(
        IAgentSessionScope parent,
        AgentCompletionDeliveryPolicy deliveryPolicy)
    {
        ArgumentNullException.ThrowIfNull(parent);
        return new(parent, deliveryPolicy);
    }
}
