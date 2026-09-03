using Parrot.Security;

namespace Parrot.Agent;

internal sealed class AgentPolicyLineage(AgentSession? parent, AgentPolicyLineage? ancestors)
{
    public static AgentPolicyLineage Root() => new(null, null);

    public AgentPolicyLineage Link(AgentSession linkedParent)
    {
        ArgumentNullException.ThrowIfNull(linkedParent);
        return new AgentPolicyLineage(linkedParent, this);
    }

    public SecurityProfile Resolve(SecurityProfile securityProfile)
    {
        ArgumentNullException.ThrowIfNull(securityProfile);
        if (parent is null)
        {
            return securityProfile;
        }

        var selected = parent.Selection();
        return ancestors?.Resolve(selected.SecurityProfile).RestrictWith(securityProfile)
            ?? throw new AgentRegistryException("agent policy lineage is missing its ancestors");
    }

    public int CountProfile(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (parent is null)
        {
            return 0;
        }

        var occurrence = string.Equals(parent.Selection().Profile.Id, profileId, StringComparison.Ordinal) ? 1 : 0;
        return occurrence + (ancestors?.CountProfile(profileId)
            ?? throw new AgentRegistryException("agent policy lineage is missing its ancestors"));
    }
}
