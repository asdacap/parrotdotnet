using Parrot.Agent;
using Parrot.Config;
using Parrot.Store;

namespace Parrot.Context;

internal sealed class AgentHistoryProvider(UserSessionResources resources, IPromptTemplateCatalog templates) : ISystemPromptProvider
{
    public string Key => "runtime:system-context:93-agent-history";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var path = resources.AgentHistoryFile(identity.SessionId);
        return new StaticSystemPrompt(templates.Render("context.agent-history", [
            new PromptTemplateArgument("path", path),
        ]));
    }
}
