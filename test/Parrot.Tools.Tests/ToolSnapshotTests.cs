using Parrot.Agent;
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
            tools.Add(new FlaggedTool(names[index], flags[index]));
        }

        var snapshot = ToolSnapshot.Document(
            tools,
            [.. names.Select(_ => true)],
            new TestToolDefinitionsFixture([.. names]).Definitions);

        var surviving = snapshot.EnabledAfterInterruption();
        var survivingNames = surviving.Tools.Select(tool => tool.Name).ToArray();
        var survivingDefinitions = surviving.Definitions.Select(definition => definition.Name).ToArray();

        _ = await Assert.That(survivingNames.Length).IsEqualTo(expected.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            _ = await Assert.That(survivingNames).Contains(expected[index]);
            _ = await Assert.That(survivingDefinitions).Contains(expected[index]);
        }
    }

    [Test]
    public async Task EnabledAfterInterruption_keeps_the_original_snapshot_untouched()
    {
        var snapshot = ToolSnapshot.Document(
            [new FlaggedTool("survivor", true), new FlaggedTool("worker", false)],
            [true, true],
            new TestToolDefinitionsFixture("survivor", "worker").Definitions);

        var surviving = snapshot.EnabledAfterInterruption();

        _ = await Assert.That(surviving.Tools.Select(tool => tool.Name).Single()).IsEqualTo("survivor");
        _ = await Assert.That(snapshot.Tools.Count).IsEqualTo(2);
        _ = await Assert.That(snapshot.Find("worker")).IsNotNull();
    }

    private sealed class FlaggedTool(string name, bool survives) : ITool
    {
        public string Name => name;

        public bool IsEnabledAfterInterruption => survives;

        public Task<ToolExecutionResult> Execute(
            ToolInvocation invocation,
            AgentTurnSelection selection,
            CancellationToken cancellationToken) =>
            Task.FromResult<ToolExecutionResult>(string.Empty);
    }
}
