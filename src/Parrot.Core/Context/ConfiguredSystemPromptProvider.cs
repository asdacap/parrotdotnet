using Parrot.Agent;

namespace Parrot.Context;

internal sealed class ConfiguredSystemPromptProvider(string key, string prompt) : ISystemPromptProvider
{
    public string Key { get; } = key;

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new StaticSystemPrompt(prompt);
    }
}
