using Parrot.Agent;
using Parrot.Config;
using Parrot.Store;

namespace Parrot.Context;

internal sealed class ScratchDirectoryProvider(AgentScratchDirectory scratch, IPromptTemplateCatalog templates) : ISystemPromptProvider
{
    public string Key => "runtime:user-session-context:01-agent-scratch";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new StaticSystemPrompt(templates.Render("context.agent-scratch", [
            new PromptTemplateArgument("path", scratch.Root),
        ]));
    }
}
