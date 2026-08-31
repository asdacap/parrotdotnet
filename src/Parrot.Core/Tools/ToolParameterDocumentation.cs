using System.Collections.ObjectModel;

namespace Parrot.Tools;

internal sealed class ToolParameterDocumentation(
    string description,
    IReadOnlyDictionary<string, ToolParameterDocumentation> properties)
{
    public string Description { get; } = description;

    public IReadOnlyDictionary<string, ToolParameterDocumentation> Properties { get; } =
        new ReadOnlyDictionary<string, ToolParameterDocumentation>(
            properties.ToDictionary(
                property => property.Key,
                property => property.Value.Copy(),
                StringComparer.Ordinal));

    public ToolParameterDocumentation Copy() => new(Description, Properties);
}
