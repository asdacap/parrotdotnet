using System.Text;
using Parrot.Agent;

namespace Parrot.Context;

internal sealed class SubagentsProvider(ProfileRegistry profiles) : ISystemPromptProvider
{
    public string Key => "runtime:system-context:09-subagents";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var childProfiles = profiles.Children;

        if (childProfiles.Count == 0)
        {
            return new StaticSystemPrompt("Available subagents: none");
        }

        var subagents = new StringBuilder("Available subagents; delegate according to their configured usage:");
        foreach (var profile in childProfiles)
        {
            _ = subagents.Append("\n- ").Append(profile.Id).Append(": ").Append(profile.Usage);
        }

        return new StaticSystemPrompt(subagents.ToString());
    }
}
