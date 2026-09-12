using System.Runtime.InteropServices;
using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Context;

internal sealed class PlatformProvider(IPromptTemplateCatalog templates) : ISystemPromptProvider
{
    public string Key => "runtime:system-context:07-platform";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new StaticSystemPrompt(templates.Render("context.platform", [
            new PromptTemplateArgument("platform", RuntimeInformation.RuntimeIdentifier),
        ]));
    }
}
