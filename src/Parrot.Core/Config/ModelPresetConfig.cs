using System.Collections.ObjectModel;

namespace Parrot.Config;

internal sealed record ModelPresetConfig
{
    public ModelPresetConfig(string model, IEnumerable<KeyValuePair<string, string>> modelAliases)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(modelAliases);

        Model = model;
        var aliases = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in modelAliases)
        {
            aliases.Add(entry.Key, entry.Value);
        }

        ModelAliases = new ReadOnlyDictionary<string, string>(aliases);
    }

    public string Model { get; }

    public IReadOnlyDictionary<string, string> ModelAliases { get; }
}
