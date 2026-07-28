using System.Collections.ObjectModel;

namespace Parrot.Llm;

internal sealed class ModelAliasSnapshot
{
    internal ModelAliasSnapshot(IEnumerable<ModelAliasDefinition> definitions)
    {
        var copied = new SortedDictionary<string, ModelAliasDefinition>(StringComparer.Ordinal);

        foreach (var definition in definitions)
        {
            copied.Add(definition.Name, definition);
        }

        Definitions = new ReadOnlyDictionary<string, ModelAliasDefinition>(copied);
    }

    public ReadOnlyDictionary<string, ModelAliasDefinition> Definitions { get; }

    public ModelAliasDefinition? Find(string name) =>
        Definitions.TryGetValue(name, out var definition) ? definition : null;
}
