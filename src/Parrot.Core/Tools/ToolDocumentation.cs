using System.Collections.ObjectModel;

namespace Parrot.Tools;

internal sealed class ToolDocumentation(
    string description,
    IReadOnlyDictionary<string, ToolParameterDocumentation> parameters)
{
    public string Description { get; } = description;

    public IReadOnlyDictionary<string, ToolParameterDocumentation> Parameters { get; } =
        new ReadOnlyDictionary<string, ToolParameterDocumentation>(
            parameters.ToDictionary(
                parameter => parameter.Key,
                parameter => parameter.Value.Copy(),
                StringComparer.Ordinal));
}
