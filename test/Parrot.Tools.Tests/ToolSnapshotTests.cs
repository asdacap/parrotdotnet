using Parrot.Agent;
using Parrot.Config;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class ToolSnapshotTests
{
    [Test]
    [Arguments(new[] { true, false }, new string[] { "settlement", "work" }, new string[] { "settlement" })]
    [Arguments(new[] { false, false }, new string[] { "first", "second" }, new string[0])]
    [Arguments(new[] { true, true }, new string[] { "first", "second" }, new string[] { "first", "second" })]
    public async Task EnabledAfterInterruption_keeps_only_flagged_tools(
        bool[] flags,
        string[] names,
        string[] expected)
    {
        var tools = new List<ITool>();
        for (var index = 0; index < names.Length; index++)
        {
            tools.Add(new FlaggedTool(names[index], flags[index]) { Aliases = [$"{names[index]}-alias"] });
        }

        var snapshot = ToolSnapshot.Document(
            tools,
            [.. names.Select(_ => true)],
            [.. names.Select(_ => TestModels.ToolDefinition)]);

        var surviving = snapshot.EnabledAfterInterruption();
        var survivingNames = surviving.Tools.Select(tool => tool.Name).ToArray();
        var survivingDefinitions = surviving.Definitions.Select(definition => definition.Name).ToArray();

        _ = await Assert.That(survivingNames.Length).IsEqualTo(expected.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            _ = await Assert.That(survivingNames).Contains(expected[index]);
            _ = await Assert.That(survivingDefinitions).Contains(expected[index]);
        }

        for (var index = 0; index < names.Length; index++)
        {
            _ = await Assert.That(surviving.Find($"{names[index]}-alias"))
                .IsSameReferenceAs(flags[index] ? tools[index] : null);
        }
    }

    [Test]
    public async Task EnabledAfterInterruption_keeps_the_original_snapshot_untouched()
    {
        var snapshot = ToolSnapshot.Document(
            [new FlaggedTool("survivor", true), new FlaggedTool("worker", false)],
            [true, true],
            [TestModels.ToolDefinition, TestModels.ToolDefinition]);

        var surviving = snapshot.EnabledAfterInterruption();

        _ = await Assert.That(surviving.Tools.Select(tool => tool.Name).Single()).IsEqualTo("survivor");
        _ = await Assert.That(snapshot.Tools.Count).IsEqualTo(2);
        _ = await Assert.That(snapshot.Find("worker")).IsNotNull();
    }

    [Test]
    public async Task Invocation_aliases_resolve_without_changing_model_definitions()
    {
        ITool tool = new FlaggedTool("work", false) { Aliases = ["shell", "terminal"] };
        var definition = TestModels.ToolDefinition;
        var snapshot = ToolSnapshot.Document([tool], [true], [definition]);

        _ = await Assert.That(snapshot.Find("work")).IsSameReferenceAs(tool);
        _ = await Assert.That(snapshot.Find("shell")).IsSameReferenceAs(tool);
        _ = await Assert.That(snapshot.Find("terminal")).IsSameReferenceAs(tool);
        _ = await Assert.That(snapshot.Find("Shell")).IsNull();
        _ = await Assert.That(snapshot.Find("unknown")).IsNull();
        _ = await Assert.That(snapshot.Tools.Single()).IsSameReferenceAs(tool);
        var documented = snapshot.Definitions.Single();
        _ = await Assert.That(documented.Name).IsEqualTo("work");
        _ = await Assert.That(documented.Description).IsEqualTo(definition.Description);
        _ = await Assert.That(documented.ParametersJson).IsEqualTo(definition.ParametersJson);
    }

    [Test]
    public async Task Unsupported_tools_do_not_resolve_by_canonical_name_or_alias()
    {
        var snapshot = ToolSnapshot.Document(
            [new FlaggedTool("work", false) { Aliases = ["shell"] }],
            [false],
            [TestModels.ToolDefinition]);

        _ = await Assert.That(snapshot.Find("work")).IsNull();
        _ = await Assert.That(snapshot.Find("shell")).IsNull();
        _ = await Assert.That(snapshot.Tools).IsEmpty();
        _ = await Assert.That(snapshot.Definitions).IsEmpty();
    }

    [Test]
    [Arguments(new string[] { "work" }, new string[0], true)]
    [Arguments(new string[] { "shell" }, new string[0], false)]
    [Arguments(new string[] { "work" }, new string[] { "work" }, false)]
    [Arguments(new string[] { "work" }, new string[] { "shell" }, true)]
    public async Task Permission_filters_use_canonical_names_for_aliases(
        string[] allowedTools,
        string[] disabledTools,
        bool permitted)
    {
        ITool tool = new FlaggedTool("work", false) { Aliases = ["shell"] };
        var snapshot = ToolSnapshot.Document([tool], [true], [TestModels.ToolDefinition]);
        var profile = new AgentProfile(
            "test",
            new ProfileConfig("Test prompt", "Test profile.", allowedTools, 2, 3, false, false, true, []),
            [],
            [],
            new HashSet<string>(disabledTools, StringComparer.Ordinal));

        var filtered = snapshot.PermittedBy(profile);

        _ = await Assert.That(filtered.Find("work")).IsSameReferenceAs(permitted ? tool : null);
        _ = await Assert.That(filtered.Find("shell")).IsSameReferenceAs(permitted ? tool : null);
        _ = await Assert.That(filtered.Definitions.Count).IsEqualTo(permitted ? 1 : 0);
        _ = await Assert.That(snapshot.Find("shell")).IsSameReferenceAs(tool);
    }

    [Test]
    [Arguments("first", "second", "shared", "shared", true)]
    [Arguments("first", "second", "shared", "shared", false)]
    [Arguments("first", "second", "second", "other", true)]
    [Arguments("first", "second", "second", "other", false)]
    [Arguments("first", "second", "other", "first", true)]
    [Arguments("first", "second", "other", "first", false)]
    public async Task Colliding_aliases_fail_closed(
        string firstName,
        string secondName,
        string firstAlias,
        string secondAlias,
        bool supported)
    {
        _ = await Assert.That(() => ToolSnapshot.Document(
            [
                new FlaggedTool(firstName, false) { Aliases = [firstAlias] },
                new FlaggedTool(secondName, false) { Aliases = [secondAlias] },
            ],
            [true, supported],
            [TestModels.ToolDefinition, TestModels.ToolDefinition]))
            .Throws<InvalidDataException>();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Duplicate_runtime_tool_names_fail_closed(bool supported)
    {
        var exception = Assert.Throws<InvalidDataException>(() => ToolSnapshot.Document(
            [new FlaggedTool("work", true), new FlaggedTool("work", true)],
            [true, supported],
            [TestModels.ToolDefinition, TestModels.ToolDefinition]));

        _ = await Assert.That(exception.Message).IsEqualTo("tool 'work' is registered more than once");
    }

    private sealed class FlaggedTool(string name, bool survives) : ITool
    {
        public string Name => name;

        public IReadOnlyList<string> Aliases { get; init; } = [];

        public bool IsEnabledAfterInterruption => survives;

        public Task<ToolExecutionResult> Execute(
            ToolInvocation invocation,
            AgentTurnSelection selection,
            CancellationToken cancellationToken) =>
            Task.FromResult<ToolExecutionResult>(string.Empty);
    }
}
