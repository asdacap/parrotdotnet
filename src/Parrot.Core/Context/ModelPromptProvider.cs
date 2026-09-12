using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Context;

internal sealed class ModelPromptProvider(IReadOnlyDictionary<string, string> augmentations, IPromptTemplateCatalog templates) : ISystemPromptProvider
{
    private readonly Dictionary<string, string> _augmentations = new(augmentations, StringComparer.Ordinal);

    public string Key => "runtime:system-context:10-model-prompt";

    public ISystemPrompt Materialize(AgentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new ModelPrompt(_augmentations, templates);
    }
}
