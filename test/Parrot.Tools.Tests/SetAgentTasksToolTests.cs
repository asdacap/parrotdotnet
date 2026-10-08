using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Security;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class SetAgentTasksToolTests : IDisposable
{
    private const string Declarations =
        """[{"name":"build","description":"Build it","payload":"Run the build","acceptance_criteria":"It builds","hidden":true},{"name":"test","dependencies":["build"],"description":"Test it","payload":"Run the tests","acceptance_criteria":"Tests pass","state":"succeeded","result":"green"}]""";

    private readonly string _root = Directory.CreateTempSubdirectory("parrot-set-agent-tasks-tool-tests-").FullName;

    public SetAgentTasksToolTests()
    {
        File.WriteAllText(Path.Combine(_root, "artifact.json"), $$"""{"schema_version":1,"tasks":{{Declarations}}}""");
        File.WriteAllText(Path.Combine(_root, "malformed.json"), "{}");
        _ = File.CreateSymbolicLink(Path.Combine(_root, "alias.json"), Path.Combine(_root, "artifact.json"));
    }

    public void Dispose() => Directory.Delete(_root, true);

    [Test]
    [Arguments("{}", "error: Tool arguments require exactly one of a nonblank string 'path' or a 'tasks' array.")]
    [Arguments("{\"path\":\"artifact.json\",\"tasks\":[]}", "error: Tool arguments require exactly one of a nonblank string 'path' or a 'tasks' array.")]
    [Arguments("{\"path\":\"\"}", "error: Tool arguments require a nonblank string 'path'.")]
    [Arguments("{\"path\":null}", "error: Tool arguments require a nonblank string 'path'.")]
    [Arguments("{\"path\":\"missing.json\"}", "error: Source 'missing.json' is missing.")]
    [Arguments("{\"path\":\"malformed.json\"}", "error: schema_version is required.")]
    [Arguments("{\"path\":\"alias.json\"}", "error: Path 'alias.json' traverses a symbolic link.")]
    [Arguments("{\"tasks\":[]}", "error: tasks must not be empty.")]
    [Arguments("{\"tasks\":[{\"name\":\"a\",\"description\":\"d\",\"payload\":\"p\",\"acceptance_criteria\":\"c\",\"state\":\"blocked\"}]}", "error: tasks[0] state must be pending, running, succeeded, failed, or canceled.")]
    public async Task Rejects_invalid_input_without_setting_tasks(string arguments, string expected, CancellationToken cancellationToken)
    {
        await using var agentTasks = new RecordingTaskService(null);

        var result = await Tool(agentTasks).Execute(new ToolInvocation("call", arguments), Selection(), cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo(expected);
        _ = await Assert.That(agentTasks.Calls).IsEmpty();
    }

    [Test]
    public async Task Rejects_out_of_policy_artifacts(CancellationToken cancellationToken)
    {
        await using var agentTasks = new RecordingTaskService(null);
        var denied = Selection() with
        {
            SecurityProfile = SecurityProfile.Compose(
                false,
                [],
                [new SandboxRule(Path.Combine(_root, "artifact.json"), SandboxRuleAction.DenyRead)],
                []),
        };

        var result = await Tool(agentTasks).Execute(new ToolInvocation("call", "{\"path\":\"artifact.json\"}"), denied, cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo("error: access denied");
        _ = await Assert.That(agentTasks.Calls).IsEmpty();
    }

    [Test]
    [Arguments("{\"path\":\"artifact.json\"}")]
    [Arguments("{\"tasks\":" + Declarations + "}")]
    public async Task Sets_the_tasks_with_the_turn_selection_and_the_current_tool_batch_boundary(string arguments, CancellationToken cancellationToken)
    {
        await using var agentTasks = new RecordingTaskService(null);
        var selection = Selection();

        var result = await Tool(agentTasks).Execute(new ToolInvocation("call-7", arguments, 7), selection, cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo("AgentTask update accepted (2 tasks). State changes are delivered as notifications.");
        var call = agentTasks.Calls.Single();
        _ = await Assert.That(string.Join(",", call.Tasks.Select(task => $"{task.Name}:{task.State}:{task.Result}:{string.Join('+', task.Dependencies)}")))
            .IsEqualTo("build:Pending::,test:Succeeded:green:build");
        _ = await Assert.That(call.Tasks[0].Hidden).IsTrue();
        _ = await Assert.That(call.Tasks[1].Hidden).IsFalse();
        _ = await Assert.That(call.Selection).IsSameReferenceAs(selection);
        _ = await Assert.That(call.HistoryBoundary).IsEqualTo(new HistoryForkBoundary.BeforeToolBatch(7, "call-7"));
    }

    [Test]
    [Arguments("cycle")]
    [Arguments("shutdown")]
    public async Task Reports_a_rejected_set_as_a_tool_error(string rejection, CancellationToken cancellationToken)
    {
        Exception failure = rejection == "cycle"
            ? new ArgumentException("tasks contains a dependency cycle.")
            : new InvalidOperationException("The agent session is shutting down.");
        await using var agentTasks = new RecordingTaskService(failure);

        var result = await Tool(agentTasks).Execute(new ToolInvocation("call", "{\"tasks\":" + Declarations + "}"), Selection(), cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo($"error: {failure.Message}");
    }

    private static AgentTurnSelection Selection() => TestTurnSelection.Create(SecurityProfile.Compose(readOnly: false, [], [], []));

    private SetAgentTasksTool Tool(IAgentTaskService agentTasks) =>
        new(new ToolWorkspace(_root), agentTasks, TestModels.PromptTemplates);

    private sealed class RecordingTaskService(Exception? failure) : IAgentTaskService
    {
        internal List<(IReadOnlyList<AgentTask> Tasks, AgentTurnSelection Selection, HistoryForkBoundary HistoryBoundary)> Calls { get; } = [];

        public void SetTasks(IReadOnlyList<AgentTask> tasks, AgentTurnSelection selection, HistoryForkBoundary historyBoundary)
        {
            if (failure is not null)
            {
                throw failure;
            }

            Calls.Add((tasks, selection, historyBoundary));
        }

        public IReadOnlyList<AgentTask> Snapshot() => [];

        public AgentTaskDetail? CaptureDetail(string name) => throw new NotSupportedException();

        public void ApplyVisibilityChanges(IReadOnlyList<AgentTask> previous, IReadOnlyList<AgentTask> incoming) =>
            throw new NotSupportedException();

        public Task Settle() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
