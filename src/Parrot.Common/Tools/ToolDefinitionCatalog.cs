using System.Collections.ObjectModel;

namespace Parrot.Tools;

internal sealed class ToolDefinitionCatalog(IReadOnlyDictionary<string, IToolDefinition> definitions)
{
    private readonly ReadOnlyDictionary<string, IToolDefinition> _definitions =
        new(new Dictionary<string, IToolDefinition>(definitions, StringComparer.Ordinal));

    public IReadOnlyDictionary<string, IToolDefinition> Definitions => _definitions;

    public IToolDefinition Describe(string name) =>
        _definitions.TryGetValue(name, out var definition)
            ? definition
            : throw new InvalidDataException($"tools.{name} is not defined");
}
