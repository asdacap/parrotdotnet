using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;

namespace Parrot.Queues;

internal sealed class QueueGuidanceProvider(IPromptTemplateCatalog templates) : ISystemPromptProvider
{
    public string Key => "runtime:system-context:09-queue-guidance";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new QueueGuidancePrompt(templates);
    }
}
