using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Context;

internal sealed class WorkingDirectoryProvider(string workingDirectory, IPromptTemplateCatalog templates) : ISystemPromptProvider
{
    public string Key => "runtime:system-context:12-working-directory";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new StaticSystemPrompt(templates.Render("system.working-directory", [
            new PromptTemplateArgument("working_directory", workingDirectory),
        ]));
    }
}
