using Parrot.Agent;

namespace Parrot.Context;

internal sealed class ModelPromptProvider(IReadOnlyDictionary<string, string> augmentations) : ISystemPromptProvider
{
    private readonly Dictionary<string, string> _augmentations = new(augmentations, StringComparer.Ordinal);

    public string Key => "runtime:model-prompt";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new ModelPrompt(_augmentations);
    }
}
