using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Context;

internal sealed class QueueGuidanceProvider(PromptTemplateCatalog templates) : ISystemPromptProvider
{
    public string Key => "runtime:system-context:09-queue-guidance";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new QueueGuidancePrompt(templates);
    }
}
