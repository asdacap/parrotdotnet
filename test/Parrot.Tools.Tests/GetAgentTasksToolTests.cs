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
    [Arguments("{}", """[{"name":"build","state":"succeeded","description":"Build it","hidden":true},{"name":"test","state":"failed","description":"Test it","hidden":false},{"name":"ship","state":"pending","description":"Ship it","hidden":false}]""")]
    [Arguments("{\"name\":null}", """[{"name":"build","state":"succeeded","description":"Build it","hidden":true},{"name":"test","state":"failed","description":"Test it","hidden":false},{"name":"ship","state":"pending","description":"Ship it","hidden":false}]""")]
    [Arguments("{\"name\":\"build\"}", """{"name":"build","dependencies":[],"description":"Build it","payload":"Run the build","acceptance_criteria":"It builds","model":"fast","hidden":true,"agent_name":"worker-build","state":"succeeded","result":"built ✓ \"quoted\""}""")]
    [Arguments("{\"name\":\"test\"}", """{"name":"test","dependencies":["build"],"description":"Test it","payload":"Run the tests","acceptance_criteria":"Tests pass","hidden":false,"agent_name":"worker-test","state":"failed","failure":"red"}""")]
    [Arguments("{\"name\":\"ship\"}", """{"name":"ship","dependencies":["build","test"],"description":"Ship it","payload":[{"name":"tag","dependencies":[],"description":"Tag it","payload":"Tag the release","acceptance_criteria":"Tagged","hidden":true}],"acceptance_criteria":"Shipped","hidden":false,"state":"pending"}""")]
    [Arguments("{\"name\":\"missing\"}", "error: AgentTask 'missing' does not exist.")]
    [Arguments("{\"name\":\" \"}", "error: Tool arguments require a nonblank string 'name'.")]
    public async Task Lists_every_task_or_returns_the_named_task_in_full(string arguments, string expected, CancellationToken cancellationToken)
    {
        var tasks = AgentTaskParser.ParseTaskSet("""
            [
              {"name":"build","description":"Build it","payload":"Run the build","acceptance_criteria":"It builds","model":"fast","hidden":true,"state":"succeeded","result":"built ✓ \"quoted\""},
              {"name":"test","dependencies":["build"],"description":"Test it","payload":"Run the tests","acceptance_criteria":"Tests pass","state":"failed","failure":"red"},
              {"name":"ship","dependencies":["build","test"],"description":"Ship it","payload":[{"name":"tag","description":"Tag it","payload":"Tag the release","acceptance_criteria":"Tagged","hidden":true}],"acceptance_criteria":"Shipped"}
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
            new TestProfileFixture().Profile,
            SecurityProfile.Compose(readOnly: false, [], [], []));
    }

    private sealed class SnapshotTaskService(IReadOnlyList<AgentTask> tasks) : IAgentTaskService
    {
        public void SetTasks(IReadOnlyList<AgentTask> tasks, AgentTurnSelection selection, HistoryForkBoundary historyBoundary) =>
            throw new NotSupportedException();

        public IReadOnlyList<AgentTask> Snapshot() => tasks;

        public AgentTaskDetail? CaptureDetail(string name) =>
            tasks.FirstOrDefault(task => task.Name == name) is { } task
                ? new AgentTaskDetail(task, task.State == AgentTaskExecutionStatus.Pending ? null : "worker-" + name)
                : null;

        public void ApplyVisibilityChanges(IReadOnlyList<AgentTask> previous, IReadOnlyList<AgentTask> incoming) =>
            throw new NotSupportedException();

        public Task Settle() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
