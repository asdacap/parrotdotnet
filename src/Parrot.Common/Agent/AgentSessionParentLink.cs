namespace Parrot.Agent;

internal sealed record AgentSessionParentLink(
    IAgentSessionScope? Parent,
    AgentCompletionDeliveryPolicy DeliveryPolicy,
    RetainedAgentReservation? Retention)
{
    public AgentPolicyLineage PolicyLineage => Parent is { } parent
        ? parent.Session.ResolvePolicyLineage().Link(parent.Session)
        : AgentPolicyLineage.Root();

    public static AgentSessionParentLink Root() =>
        new(null, AgentCompletionDeliveryPolicy.RetainedOnly, null);

    public static AgentSessionParentLink Child(
        IAgentSessionScope parent,
        AgentCompletionDeliveryPolicy deliveryPolicy,
        RetainedAgentReservation retention)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(retention);
        return new(parent, deliveryPolicy, retention);
    }

    public void ReleaseRetention() => Retention?.Release();
}
