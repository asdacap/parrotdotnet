using Parrot.Agent;
using Parrot.Context;

namespace Parrot.Skills;

internal sealed class SkillPrompt(AgentSkills skills) : ISystemPrompt
{
    public void RenewEpoch()
    {
    }

    public string Build(AgentTurnSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return skills.BuildCatalog(selection.SecurityProfile);
    }
}
