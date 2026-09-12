using System.Collections.ObjectModel;
using Parrot.Context;

namespace Parrot.Config;

internal sealed record ModelPresetContextLimits
{
    public ModelPresetContextLimits(ContextSize? defaultLimit, IEnumerable<KeyValuePair<string, ContextSize>> modelAliases)
    {
        ArgumentNullException.ThrowIfNull(modelAliases);
        Default = defaultLimit;
        var aliases = new SortedDictionary<string, ContextSize>(StringComparer.Ordinal);
        foreach (var entry in modelAliases)
        {
            aliases.Add(entry.Key, entry.Value);
        }

        ModelAliases = new ReadOnlyDictionary<string, ContextSize>(aliases);
    }

    public ContextSize? Default { get; }

    public IReadOnlyDictionary<string, ContextSize> ModelAliases { get; }
}
