using System.Collections.ObjectModel;
using Parrot.Llm;

namespace Parrot.Tools;

internal sealed class ToolDefinitionCatalog(IReadOnlyDictionary<string, ConfiguredToolDefinition> definitions)
{
    private readonly ReadOnlyDictionary<string, ConfiguredToolDefinition> _definitions =
        new(definitions.ToDictionary(
            definition => definition.Key,
            definition => new ConfiguredToolDefinition(
                definition.Value.Description,
                definition.Value.ParametersJson),
            StringComparer.Ordinal));

    public IReadOnlyDictionary<string, ConfiguredToolDefinition> Definitions => _definitions;

    public IReadOnlyList<LLMToolDefinition> Document(IReadOnlyList<ITool> runtimeTools)
    {
        ArgumentNullException.ThrowIfNull(runtimeTools);
        var runtimeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in runtimeTools)
        {
            if (!runtimeNames.Add(tool.Name))
            {
                throw new InvalidDataException($"tool '{tool.Name}' is registered more than once");
            }
        }

        foreach (var name in runtimeNames)
        {
            if (!_definitions.ContainsKey(name))
            {
                throw new InvalidDataException($"tools.{name} is not defined");
            }
        }

        foreach (var name in _definitions.Keys)
        {
            if (!runtimeNames.Contains(name))
            {
                throw new InvalidDataException($"tools.{name} does not match a registered tool");
            }
        }

        return [.. runtimeTools.Select(tool =>
        {
            var definition = _definitions[tool.Name];
            return new LLMToolDefinition(tool.Name, tool.Describe(definition.Description), definition.ParametersJson);
        })];
    }
}
