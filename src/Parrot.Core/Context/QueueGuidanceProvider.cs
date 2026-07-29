using Parrot.Agent;

namespace Parrot.Context;

internal sealed class QueueGuidanceProvider : ISystemPromptProvider
{
    public string Key => "runtime:queue-guidance";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new QueueGuidancePrompt();
    }
}
