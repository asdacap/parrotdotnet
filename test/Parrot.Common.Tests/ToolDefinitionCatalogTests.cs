using System.Text.Json;
using Parrot.Agent;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class ToolDefinitionCatalogTests
{
    [Test]
    public async Task Configured_definition_is_forwarded_without_rewriting_schema()
    {
        const string parameters = """{"type":"object","properties":{"description":{"type":"string","description":"Semantic value."}},"required":["description"],"additionalProperties":false}""";
        var catalog = new ToolDefinitionCatalog(
            new Dictionary<string, ConfiguredToolDefinition>(StringComparer.Ordinal)
            {
                ["work"] = new ConfiguredToolDefinition("Does work.", parameters),
            });

        var definition = catalog.Document([new StructuralTool("work")]).Single();
        var described = catalog.Document([new DescribingTool("work")]).Single();

        _ = await Assert.That(definition.Description).IsEqualTo("Does work.");
        _ = await Assert.That(definition.ParametersJson).IsEqualTo(parameters);
        _ = await Assert.That(described.Description).IsEqualTo("Does work. Described by the tool.");
        _ = await Assert.That(described.ParametersJson).IsEqualTo(parameters);
        using var schema = JsonDocument.Parse(definition.ParametersJson);
        _ = await Assert.That(schema.RootElement.GetProperty("properties").GetProperty("description")
            .GetProperty("description").GetString()).IsEqualTo("Semantic value.");
    }

    [Test]
    [Arguments("missing-tool", "tools.work is not defined")]
    [Arguments("extra-tool", "tools.extra does not match a registered tool")]
    public async Task Invalid_catalog_correspondence_fails_closed(string scenario, string message)
    {
        var definitions = new Dictionary<string, ConfiguredToolDefinition>(StringComparer.Ordinal)
        {
            ["work"] = new ConfiguredToolDefinition("Does work.", """{"type":"object","additionalProperties":false}"""),
        };
        if (string.Equals(scenario, "missing-tool", StringComparison.Ordinal))
        {
            definitions.Clear();
        }
        else
        {
            definitions["extra"] = new ConfiguredToolDefinition("Does work.", """{"type":"object","additionalProperties":false}""");
        }

        var exception = Assert.Throws<InvalidDataException>(() =>
            new ToolDefinitionCatalog(definitions).Document([new StructuralTool("work")]));

        _ = await Assert.That(exception.Message).IsEqualTo(message);
    }

    [Test]
    public async Task Duplicate_runtime_tool_ids_fail_closed()
    {
        var catalog = new ToolDefinitionCatalog(
            new Dictionary<string, ConfiguredToolDefinition>(StringComparer.Ordinal)
            {
                ["work"] = new ConfiguredToolDefinition("Does work.", """{"type":"object","additionalProperties":false}"""),
            });

        var exception = Assert.Throws<InvalidDataException>(() =>
            catalog.Document([new StructuralTool("work"), new StructuralTool("work")]));

        _ = await Assert.That(exception.Message).IsEqualTo("tool 'work' is registered more than once");
    }

    private sealed class StructuralTool(string name) : ITool
    {
        public string Name => name;

        public Task<ToolExecutionResult> Execute(
            ToolInvocation invocation,
            AgentTurnSelection selection,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class DescribingTool(string name) : ITool
    {
        public string Name => name;

        public string Describe(string configuredDescription) => configuredDescription + " Described by the tool.";

        public Task<ToolExecutionResult> Execute(
            ToolInvocation invocation,
            AgentTurnSelection selection,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
