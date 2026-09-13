using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Process;
using Parrot.Queues;
using Parrot.Security;
using Parrot.Statuses;

namespace Parrot.Core.Tests;

internal sealed class ActiveWorkBlockerTests
{
    [Test]
    public async Task Reminder_observes_each_blocker_once_and_preserves_section_and_reminder_order()
    {
        var observed = new List<string>();
        var promptTemplates = new RecordingPromptTemplateCatalog();
        var blockers = new IActiveWorkBlocker[]
        {
            new RecordingBlocker("first", observed, new ActiveWorkBlockerResult("\nfirst", "first reminder")),
            new RecordingBlocker("second", observed, new ActiveWorkBlockerResult("\nsecond", "second reminder")),
        };

        var reminder = new ActiveWorkCompletionReminder(blockers, promptTemplates).Build();

        _ = await Assert.That(string.Join(',', observed)).IsEqualTo("first,second");
        _ = await Assert.That(reminder).IsEqualTo(
            "agent-session.active-work-reminder[active_work=\nfirst\nsecond]\nfirst reminder\nsecond reminder");
        _ = await Assert.That(string.Join(',', promptTemplates.RenderedIds))
            .IsEqualTo("agent-session.active-work-reminder");
    }

    [Test]
    public async Task Reminder_keeps_an_empty_rendered_result_as_blocking_and_returns_null_when_all_are_empty()
    {
        var promptTemplates = new RecordingPromptTemplateCatalog();
        var emptyResult = new RecordingBlocker("empty", [], new ActiveWorkBlockerResult(string.Empty, null));
        var emptyRenderedReminder = new ActiveWorkCompletionReminder([emptyResult], promptTemplates).Build();

        _ = await Assert.That(emptyRenderedReminder).IsEqualTo("agent-session.active-work-reminder[active_work=]");
        _ = await Assert.That(new ActiveWorkCompletionReminder(
            [new RecordingBlocker("none", [], null)], promptTemplates).Build()).IsNull();
    }

    [Test]
    public async Task Child_blocker_returns_null_without_active_descendants()
    {
        var identity = AgentIdentity.Main("owner", "owner", TestModels.PromptTemplates);
        await using var children = new ChildRegistry(identity, QueueChildAdmissionValidator.Validate);

        var result = new ChildAgentActiveWorkBlocker(children, identity).Observe();

        _ = await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Process_blocker_returns_sorted_active_process_section()
    {
        await using var processOwner = new StubProcessOwner(
        [
            new ActiveWorkObservation("process-b", "B", ActiveWorkKind.Shell, ActiveWorkState.Running),
            new ActiveWorkObservation("process-a", "A", ActiveWorkKind.Shell, ActiveWorkState.Running),
        ]);

        var result = new ProcessActiveWorkBlocker(processOwner).Observe();

        _ = await Assert.That(result?.WorkSection).IsEqualTo(
            "\nRunning processes:\n- process-a (name: A)\n- process-b (name: B)");
        _ = await Assert.That(result?.Reminder).IsNull();
    }

    [Test]
    public async Task Agent_task_blocker_returns_sorted_active_task_section()
    {
        await using var taskCatalog = new StubTaskCatalog(
        [
            new ActiveWorkObservation("task-b", "B", ActiveWorkKind.AgentTask, ActiveWorkState.Running),
            new ActiveWorkObservation("task-a", "A", ActiveWorkKind.AgentTask, ActiveWorkState.Running),
        ]);

        var result = new AgentTaskActiveWorkBlocker(taskCatalog, TestModels.PromptTemplates).Observe();

        _ = await Assert.That(result?.WorkSection).IsEqualTo(
            "\nRunning AgentTask graphs:\n- task-a (name: A)\n- task-b (name: B)");
        _ = await Assert.That(result?.Reminder).IsNull();
    }

    [Test]
    public async Task Queue_blocker_ignores_empty_queues_and_unmonitored_state_but_blocks_nonempty_queues()
    {
        var identity = AgentIdentity.Main("queue-owner", "owner", TestModels.PromptTemplates);
        await using var fixture = new AgentQueueTestFixture(identity);
        _ = fixture.Queues.Create("zulu", string.Empty);
        _ = fixture.Queues.Create("alpha", string.Empty);
        _ = await fixture.Queues.Push("zulu", ["one", "two"], QueueDirection.Back, false, CancellationToken.None);

        var result = new QueueActiveWorkBlocker(fixture.Queues, TestModels.PromptTemplates).Observe();

        _ = await Assert.That(result?.WorkSection).IsNull();
        var queueItem = TestModels.PromptTemplates.Render(
            "agent-session.nonempty-queue-item",
            [new PromptTemplateArgument("name", "zulu"), new PromptTemplateArgument("size", "2")]);
        var expected = TestModels.PromptTemplates.Render(
            "agent-session.nonempty-queues-reminder",
            [new PromptTemplateArgument("queues", queueItem)]);
        _ = await Assert.That(result?.Reminder).IsEqualTo(expected);
    }

    private sealed class RecordingBlocker(
        string id,
        List<string> observed,
        ActiveWorkBlockerResult? result) : IActiveWorkBlocker
    {
        public ActiveWorkBlockerResult? Observe()
        {
            observed.Add(id);
            return result;
        }
    }

    private sealed class RecordingPromptTemplateCatalog : IPromptTemplateCatalog
    {
        public List<string> RenderedIds { get; } = [];

        public string Render(string id, IReadOnlyList<PromptTemplateArgument> arguments)
        {
            RenderedIds.Add(id);
            return $"{id}[{string.Join(',', arguments.Select(argument => $"{argument.Name}={argument.Value}"))}]";
        }

        public string RenderSkills(string skills) => throw new NotSupportedException();

        public string RenderSelectedSkill(string name, string path, string content) => throw new NotSupportedException();

        public string RenderUnavailableSkill(string name, string message) => throw new NotSupportedException();

        public string RenderStructured(string id, Scriban.Runtime.ScriptObject arguments, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubTaskCatalog(IReadOnlyList<ActiveWorkObservation> active) : IAgentTaskRunCatalog
    {
        public IReadOnlyList<ActiveWorkObservation> Active() => active;

        public void Start(AgentTaskRunRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IReadOnlyList<AgentTaskRunSnapshot> Snapshot() => [];

        public Task Settle() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubProcessOwner(IReadOnlyList<ActiveWorkObservation> active) : IProcessOwner
    {
        public string SessionId => "owner";

        public IReadOnlyList<ActiveWorkObservation> Active() => active;

        public IShellProcessInventorySubscription SubscribeInventory() => throw new NotSupportedException();

        public ShellProcessInventorySnapshot CaptureInventory() => throw new NotSupportedException();

        public IManagedShellProcess StartUnattributed(
            string? requestedName,
            string command,
            ProcessEnvironmentOverrides environment,
            IAgentSession agent,
            SecurityProfile securityProfile,
            ShellProcessTerminalMode terminalMode) => throw new NotSupportedException();

        public IManagedShellProcess StartPipe(
            string? requestedName,
            string command,
            string originToolCallId,
            ProcessEnvironmentOverrides environment,
            IAgentSession agent,
            SecurityProfile securityProfile) => throw new NotSupportedException();

        public IManagedShellProcess Start(
            string? requestedName,
            string command,
            string originToolCallId,
            ProcessEnvironmentOverrides environment,
            IAgentSession agent,
            SecurityProfile securityProfile,
            ShellProcessTerminalMode terminalMode) => throw new NotSupportedException();

        public IManagedShellProcess Claim(string name) => throw new NotSupportedException();

        public Task<ShellWaitResult> WriteStdin(
            string name,
            string input,
            TimeSpan yieldAfter,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public IReadOnlyList<ShellProcessStatusSnapshot> Snapshot() => [];

        public Task Settle() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
