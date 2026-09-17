using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class ToolDefinitionCatalogTests
{
    [Test]
    [Arguments("work", null)]
    [Arguments("missing", "tools.missing is not defined")]
    public async Task Describe_forwards_the_configured_definition_or_fails_closed(string name, string? failure)
    {
        const string parameters = """{"type":"object","properties":{"description":{"type":"string","description":"Semantic value."}},"required":["description"],"additionalProperties":false}""";
        var catalog = new ToolDefinitionCatalog(
            new Dictionary<string, IToolDefinition>(StringComparer.Ordinal)
            {
                ["work"] = new ConfiguredToolDefinition("Does work.", parameters),
            });

        if (failure is not null)
        {
            var exception = Assert.Throws<InvalidDataException>(() => catalog.Describe(name));
            _ = await Assert.That(exception.Message).IsEqualTo(failure);
            return;
        }

        var definition = catalog.Describe(name);

        _ = await Assert.That(definition.Description).IsEqualTo("Does work.");
        _ = await Assert.That(definition.ParametersJson).IsEqualTo(parameters);
    }
}
