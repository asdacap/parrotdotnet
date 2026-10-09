using Parrot.Agent;
using Parrot.Config;
using Parrot.Llm;

namespace Parrot.Context;

internal sealed class ModelPromptProvider(ModelProfiles profiles, IModelRouter router, IPromptTemplateCatalog templates) : ISystemPromptProvider
{
    public string Key => "runtime:system-context:10-model-prompt";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new ModelPrompt(profiles, router, templates);
    }
}
