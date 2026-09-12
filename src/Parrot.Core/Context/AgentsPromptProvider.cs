using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Context;

internal sealed class AgentsPromptProvider(string workingDirectory, string configDirectory, PromptTemplateCatalog templates) : ISystemPromptProvider
{
    public string Key => "runtime:system-context:07a-agents";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new AgentsPrompt(workingDirectory, configDirectory, templates);
    }
}
