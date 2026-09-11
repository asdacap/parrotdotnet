using Parrot.Agent;
using Parrot.Config;
using Scriban.Runtime;

namespace Parrot.Context;

internal sealed class SubagentsProvider(ProfileRegistry profiles, PromptTemplateCatalog templates) : ISystemPromptProvider
{
    public string Key => "runtime:system-context:09-subagents";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var subagents = new ScriptArray();
        foreach (var profile in profiles.Children)
        {
            subagents.Add(new ScriptObject
            {
                ["id"] = profile.Id,
                ["usage"] = profile.Usage,
            });
        }

        return new StaticSystemPrompt(templates.RenderStructured(
            "context.subagents", new ScriptObject { ["subagents"] = subagents }, CancellationToken.None));
    }
}
