using Parrot.Agent;

namespace Parrot.Context;

internal sealed class BasePromptProvider(string basePrompt) : ISystemPromptProvider
{
    public string Key => "runtime:system-context:01-base";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new StaticSystemPrompt(basePrompt);
    }
}
