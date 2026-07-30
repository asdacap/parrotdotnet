using Parrot.Agent;

namespace Parrot.Context;

internal sealed class AgentsPromptProvider(string workingDirectory, string configDirectory) : ISystemPromptProvider
{
    public string Key => "runtime:system-context:02-agents";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new AgentsPrompt(workingDirectory, configDirectory);
    }
}
