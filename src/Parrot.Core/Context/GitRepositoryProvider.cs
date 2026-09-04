using Parrot.Agent;
using Parrot.Config;
using Parrot.Store;

namespace Parrot.Context;

internal sealed class GitRepositoryProvider(ProjectWorkspace workspace, PromptTemplateCatalog templates) : ISystemPromptProvider
{
    public string Key => "runtime:system-context:06a-git-repository";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new StaticSystemPrompt(templates.Render("context.git-repository", [
            new PromptTemplateArgument("is_repository", workspace.IsGitRepository ? "true" : "false"),
        ]));
    }
}
