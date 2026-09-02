using System.Text;
using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Context;

internal sealed class SubagentsProvider(ProfileRegistry profiles, PromptTemplateCatalog templates) : ISystemPromptProvider
{
    public string Key => "runtime:system-context:09-subagents";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var childProfiles = profiles.Children;

        if (childProfiles.Count == 0)
        {
            return new StaticSystemPrompt(templates.Render("context.subagents-none", []));
        }

        var subagents = new StringBuilder(templates.Render("context.subagents-header", []));
        foreach (var profile in childProfiles)
        {
            _ = subagents.Append(templates.Render("context.subagent", [
                new PromptTemplateArgument("id", profile.Id),
                new PromptTemplateArgument("usage", profile.Usage),
            ]));
        }

        return new StaticSystemPrompt(subagents.ToString());
    }
}
