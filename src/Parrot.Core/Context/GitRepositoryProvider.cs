using Parrot.Agent;
using Parrot.Config;
using Parrot.Store;

namespace Parrot.Context;

internal sealed class GitRepositoryProvider(ProjectWorkspace workspace, IPromptTemplateCatalog templates) : ISystemPromptProvider
{
    public string Key => "runtime:system-context:13-git-repository";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new StaticSystemPrompt(templates.Render("context.git-repository", [
            new PromptTemplateArgument("is_repository", workspace.IsGitRepository ? "true" : "false"),
        ]));
    }
}
