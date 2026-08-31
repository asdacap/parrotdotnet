using System.Text.Json;
using Parrot.Agent;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class ToolDocumentationCatalogTests
{
    [Test]
    public async Task Documentation_is_inserted_without_changing_structural_constraints()
    {
        var catalog = Catalog(new ToolDocumentation(
            "Does work.",
            Parameters(("items", Parameter("Work items.", Parameters(("value", Parameter("Item value."))))))));
        var tool = new StructuralTool(
            "work",
            """{"type":"object","properties":{"items":{"type":"array","minItems":1,"items":{"type":"object","properties":{"value":{"type":"string","minLength":2}},"required":["value"],"additionalProperties":false}}},"required":["items"],"additionalProperties":false}""");

        var definition = catalog.Document([tool]).Single();

        _ = await Assert.That(definition.Description).IsEqualTo("Does work.");
        using var schema = JsonDocument.Parse(definition.ParametersJson);
        var root = schema.RootElement;
        var items = root.GetProperty("properties").GetProperty("items");
        var value = items.GetProperty("items").GetProperty("properties").GetProperty("value");
        _ = await Assert.That(items.GetProperty("description").GetString()).IsEqualTo("Work items.");
        _ = await Assert.That(value.GetProperty("description").GetString()).IsEqualTo("Item value.");
        _ = await Assert.That(value.GetProperty("minLength").GetInt32()).IsEqualTo(2);
        _ = await Assert.That(root.GetProperty("required")[0].GetString()).IsEqualTo("items");
        _ = await Assert.That(root.GetProperty("additionalProperties").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task A_schema_property_named_description_is_not_mistaken_for_schema_prose()
    {
        var catalog = Catalog(new ToolDocumentation(
            "Does work.",
            Parameters(("description", Parameter("Semantic value.")))));
        var tool = new StructuralTool(
            "work",
            """{"type":"object","properties":{"description":{"type":"string"}},"additionalProperties":false}""");

        var definition = catalog.Document([tool]).Single();

        using var schema = JsonDocument.Parse(definition.ParametersJson);
        _ = await Assert.That(schema.RootElement.GetProperty("properties").GetProperty("description")
            .GetProperty("description").GetString()).IsEqualTo("Semantic value.");
    }

    [Test]
    public async Task Parameterless_and_dictionary_schemas_are_documented_without_invented_children()
    {
        var parameterless = new ToolDocumentation("Checks status.", Parameters());
        var dictionary = new ToolDocumentation(
            "Runs work.",
            Parameters(("env", Parameter("Environment overrides."))));
        var catalog = new ToolDocumentationCatalog(new Dictionary<string, ToolDocumentation>(StringComparer.Ordinal)
        {
            ["status"] = parameterless,
            ["run"] = dictionary,
        });

        var definitions = catalog.Document([
            new StructuralTool("status", """{"type":"object","additionalProperties":false}"""),
            new StructuralTool("run", """{"type":"object","properties":{"env":{"type":"object","additionalProperties":{"type":"string"}}},"additionalProperties":false}"""),
        ]);

        _ = await Assert.That(definitions.Count).IsEqualTo(2);
        using var schema = JsonDocument.Parse(definitions[1].ParametersJson);
        var environment = schema.RootElement.GetProperty("properties").GetProperty("env");
        _ = await Assert.That(environment.GetProperty("description").GetString()).IsEqualTo("Environment overrides.");
        _ = await Assert.That(environment.GetProperty("additionalProperties").GetProperty("type").GetString())
            .IsEqualTo("string");
    }

    [Test]
    [Arguments("missing-tool", "tools.work is not documented")]
    [Arguments("extra-tool", "tools.extra does not match a registered tool")]
    [Arguments("missing-property", "tools.work.parameters.value is not documented")]
    [Arguments("extra-property", "tools.work.parameters.extra does not match a schema property")]
    [Arguments("invalid-nesting", "tools.work.parameters.value.properties does not match an object schema")]
    [Arguments("residual-description", "tool schema at tools.work.parameters.value contains a description outside configuration")]
    public async Task Invalid_catalog_or_schema_correspondence_fails_closed(string scenario, string message)
    {
        var schema = """{"type":"object","properties":{"value":{"type":"string"}},"additionalProperties":false}""";
        var documentation = new Dictionary<string, ToolDocumentation>(StringComparer.Ordinal)
        {
            ["work"] = new ToolDocumentation("Does work.", Parameters(("value", Parameter("A value.")))),
        };
        IReadOnlyList<ITool> tools = [new StructuralTool("work", schema)];

        switch (scenario)
        {
            case "missing-tool":
                documentation.Clear();
                break;
            case "extra-tool":
                documentation["extra"] = new ToolDocumentation("Extra.", Parameters());
                break;
            case "missing-property":
                documentation["work"] = new ToolDocumentation("Does work.", Parameters());
                break;
            case "extra-property":
                documentation["work"] = new ToolDocumentation(
                    "Does work.",
                    Parameters(("value", Parameter("A value.")), ("extra", Parameter("Extra."))));
                break;
            case "invalid-nesting":
                documentation["work"] = new ToolDocumentation(
                    "Does work.",
                    Parameters(("value", Parameter("A value.", Parameters(("nested", Parameter("Nested.")))))));
                break;
            case "residual-description":
                tools = [new StructuralTool(
                    "work",
                    """{"type":"object","properties":{"value":{"type":"string","description":"C# prose"}},"additionalProperties":false}""")];
                break;
            default:
                throw new InvalidOperationException("Unknown scenario.");
        }

        var catalog = new ToolDocumentationCatalog(documentation);
        var exception = Assert.Throws<InvalidDataException>(() => catalog.Document(tools));
        _ = await Assert.That(exception.Message).IsEqualTo(message);
    }

    [Test]
    public async Task Duplicate_runtime_tool_ids_fail_closed()
    {
        var catalog = Catalog(new ToolDocumentation("Does work.", Parameters()));
        ITool[] tools = [new StructuralTool("work", "{}"), new StructuralTool("work", "{}")];

        var exception = Assert.Throws<InvalidDataException>(() => catalog.Document(tools));

        _ = await Assert.That(exception.Message).IsEqualTo("tool 'work' is registered more than once");
    }

    private static ToolDocumentationCatalog Catalog(ToolDocumentation documentation) => new(
        new Dictionary<string, ToolDocumentation>(StringComparer.Ordinal) { ["work"] = documentation });

    private static Dictionary<string, ToolParameterDocumentation> Parameters(
        params (string Name, ToolParameterDocumentation Documentation)[] parameters) =>
        parameters.ToDictionary(parameter => parameter.Name, parameter => parameter.Documentation, StringComparer.Ordinal);

    private static ToolParameterDocumentation Parameter(
        string description,
        IReadOnlyDictionary<string, ToolParameterDocumentation> properties) => new(description, properties);

    private static ToolParameterDocumentation Parameter(string description) => new(description, Parameters());

    private sealed class StructuralTool(string name, string parametersJson) : ITool
    {
        public string Name => name;

        public string ParametersJson => parametersJson;

        public Task<ToolExecutionResult> Execute(
            ToolInvocation invocation,
            AgentTurnSelection selection,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
