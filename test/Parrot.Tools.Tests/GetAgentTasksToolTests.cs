using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Llm;
using Parrot.Security;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class GetAgentTasksToolTests
{
    [Test]
    [Arguments("{}", """[{"name":"build","state":"succeeded","description":"Build it"},{"name":"test","state":"failed","description":"Test it"},{"name":"ship","state":"pending","description":"Ship it"}]""")]
    [Arguments("{\"name\":null}", """[{"name":"build","state":"succeeded","description":"Build it"},{"name":"test","state":"failed","description":"Test it"},{"name":"ship","state":"pending","description":"Ship it"}]""")]
    [Arguments("{\"name\":\"build\"}", """{"name":"build","dependencies":[],"description":"Build it","payload":"Run the build","acceptance_criteria":"It builds","model":"fast","state":"succeeded","result":"built ✓ \"quoted\""}""")]
    [Arguments("{\"name\":\"test\"}", """{"name":"test","dependencies":["build"],"description":"Test it","payload":"Run the tests","acceptance_criteria":"Tests pass","state":"failed","failure":"red"}""")]
    [Arguments("{\"name\":\"ship\"}", """{"name":"ship","dependencies":["build","test"],"description":"Ship it","payload":[{"name":"tag","dependencies":[],"description":"Tag it","payload":"Tag the release","acceptance_criteria":"Tagged"}],"acceptance_criteria":"Shipped","state":"pending"}""")]
    [Arguments("{\"name\":\"missing\"}", "error: AgentTask 'missing' does not exist.")]
    [Arguments("{\"name\":\" \"}", "error: Tool arguments require a nonblank string 'name'.")]
    public async Task Lists_every_task_or_returns_the_named_task_in_full(string arguments, string expected, CancellationToken cancellationToken)
    {
        var tasks = AgentTaskParser.ParseTaskSet("""
            [
              {"name":"build","description":"Build it","payload":"Run the build","acceptance_criteria":"It builds","model":"fast","state":"succeeded","result":"built ✓ \"quoted\""},
              {"name":"test","dependencies":["build"],"description":"Test it","payload":"Run the tests","acceptance_criteria":"Tests pass","state":"failed","failure":"red"},
              {"name":"ship","dependencies":["build","test"],"description":"Ship it","payload":[{"name":"tag","description":"Tag it","payload":"Tag the release","acceptance_criteria":"Tagged"}],"acceptance_criteria":"Shipped"}
            ]
            """);
        await using var agentTasks = new SnapshotTaskService(tasks);
        await using var emptyTasks = new SnapshotTaskService([]);

        var result = await new GetAgentTasksTool(agentTasks).Execute(new ToolInvocation("call", arguments), Selection(), cancellationToken);
        var empty = await new GetAgentTasksTool(emptyTasks).Execute(new ToolInvocation("call", "{}"), Selection(), cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo(expected);
        _ = await Assert.That(empty.Text).IsEqualTo("[]");
    }

    private static AgentTurnSelection Selection()
    {
        ILLMProvider provider = new UnusedProvider();
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        return new AgentTurnSelection(
            new ModelSelector(model.Selector),
            TestModels.Resolve(model),
            new TestProfileFixture().Mode,
            SecurityProfile.Compose(readOnly: false, [], [], []));
    }

    private sealed class SnapshotTaskService(IReadOnlyList<AgentTask> tasks) : IAgentTaskService
    {
        public void SetTasks(IReadOnlyList<AgentTask> tasks, AgentTurnSelection selection, HistoryForkBoundary historyBoundary) =>
            throw new NotSupportedException();

        public IReadOnlyList<AgentTask> Snapshot() => tasks;

        public Task Settle() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
