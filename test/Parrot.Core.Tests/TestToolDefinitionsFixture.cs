using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class TestToolDefinitionsFixture(params string[] names)
{
    public ToolDefinitionCatalog Definitions { get; } = new(
        names.ToDictionary(
            name => name,
            IToolDefinition (_) => new ConfiguredToolDefinition(
                "Test tool.",
                """{"type":"object","additionalProperties":false}"""),
            StringComparer.Ordinal));
}
