using System.Collections.Concurrent;
using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Diagnostics;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Security;
using Parrot.State;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class AgentTaskServiceTests : IAsyncDisposable
{
    private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");
    private readonly IEventBroker _broker = new EventBroker();
    private readonly List<IAgentRegistry> _registries = [];
    private readonly IEventRepository _repository;

    public AgentTaskServiceTests() => _repository = new EventRepository(_database);

    public async ValueTask DisposeAsync()
    {
        foreach (var registry in _registries)
        {
            await registry.DisposeAsync().ConfigureAwait(false);
        }

        _broker.Dispose();
        _database.Dispose();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Child_scope_diagnostics_preserve_creation_result_and_hide_failure_payload(
        bool fail,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "parrot-child-diagnostics", Guid.NewGuid().ToString("n"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            var resources = new UserSessionResources(
                new StatePaths(Path.Combine(directory, "state"), Path.Combine(directory, "config"), Path.Combine(directory, "data")),
                UserSessionId.Parse("session-diagnostics"),
                ProjectWorkspace.FromLaunchDirectory(directory));
            using var diagnostics = FileDiagnosticLog.OpenSession(resources, "test", TextWriter.Null, TimeProvider.System);
            var runtime = Runtime(new AgentTaskQueueProvider([]), cancellationToken);
            await using var runtimeRegistry = runtime.Registry;
            var factory = new DiagnosticChildFactory(runtime.ParentScope, fail);
            await using IAgentRegistry registry = new AgentRegistry(
                factory,
                _broker,
                _repository,
                new TestProfileFixture().Registry,
                TestModels.PromptTemplates,
                new RetainedAgentBudget(10),
                diagnostics,
                cancellationToken);
            var identity = AgentIdentity.Child(
                "agent-child", AgentIdentity.Main(runtime.Parent.SessionId, "secret-parent", TestModels.PromptTemplates), "secret-child", 1, AgentScope.Empty(TestModels.PromptTemplates), TestModels.PromptTemplates);
            using var dependencies = TestModels.Dependencies(runtime.Parent.Identity, _broker, _repository, cancellationToken);
            IAgentSessionScope CreateChild() => registry.CreateChildScope(
                identity,
                AgentSessionParentLink.Child(runtime.ParentScope, AgentCompletionDeliveryPolicy.RetainedOnly, registry.ReserveRetainedAgent()),
                runtime.Selection.RequestedModel,
                dependencies.Profile,
                dependencies.Profile.Profile.SecurityProfile,
                _repository,
                cancellationToken);
            if (fail)
            {
                _ = await Assert.That(CreateChild).Throws<InvalidOperationException>();
            }
            else
            {
                _ = await Assert.That(CreateChild()).IsSameReferenceAs(runtime.ParentScope);
            }

            var log = await File.ReadAllTextAsync(resources.LogPath, cancellationToken);
            _ = await Assert.That(log).Contains("event=\"child_scope_start\"")
                .And.Contains("event=\"child_scope_completed\"")
                .And.Contains(fail ? "outcome=\"failed\"" : "outcome=\"succeeded\"")
                .And.Contains("agent=\"agent-child\"")
                .And.DoesNotContain("secret-");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Runs_tasks_in_dependency_order_notifies_the_owner_and_resets_dependents_of_a_reset_task(
        CancellationToken cancellationToken)
    {
        var runs = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var provider = new AgentTaskReplyProvider((request, _) =>
        {
            if (!AgentTaskReplyProvider.IsTaskAgent(request))
            {
                return Task.FromResult("noted");
            }

            var name = TaskName(request);
            return Task.FromResult(Accept($"{name} done {runs.AddOrUpdate(name, 1, (_, count) => count + 1)}"));
        });
        var fixture = Fixture(provider, 5, cancellationToken);
        await using var registry = fixture.Runtime.Registry;
        await using var service = fixture.Service;

        _ = await Assert.That(() => Set(fixture, Declare("b", ",\"dependencies\":[\"missing\"]"))).Throws<ArgumentException>()
            .WithMessage("tasks task 'b' has missing sibling dependency 'missing'.");
        _ = await Assert.That(service.Snapshot()).IsEmpty();
        Set(fixture, Declare("a", string.Empty), Declare("b", ",\"dependencies\":[\"a\"]"), Declare("c", ",\"dependencies\":[\"a\"]"));
        _ = await Assert.That(States(service.Snapshot())).IsEqualTo("a:Running,b:Pending,c:Pending");
        _ = await Assert.That(() => Set(fixture, Declare("a", ",\"dependencies\":[\"b\"]"))).Throws<ArgumentException>()
            .WithMessage("tasks contains a dependency cycle.");

        var completed = await WaitFor(service, tasks => tasks.All(task => task.State == AgentTaskExecutionStatus.Succeeded), cancellationToken);
        _ = await Assert.That(string.Join(",", completed.Select(task => task.Result))).IsEqualTo("a done 1,b done 1,c done 1");
        var notifications = await WaitForAdmitted(fixture.Runtime.Parent.SessionId, "All AgentTasks are complete:\n- a: succeeded\n- b: succeeded\n- c: succeeded", cancellationToken);
        _ = await Assert.That(notifications).Contains("AgentTask states changed by your update:\n- a: running\n- b: pending\n- c: pending")
            .And.Contains("AgentTask succeeded: b\nDescription: Do b\n\nResult:\nb done 1");
        var dependentPrompt = AgentTaskReplyProvider.Prompt(provider.Requests.First(request => AgentTaskReplyProvider.IsTaskAgent(request) && TaskName(request) == "b"));
        _ = await Assert.That(dependentPrompt).Contains("\n[a] Do a\nResult: a done 1")
            .And.Contains("[c] Do c");

        Set(fixture, Declare("a", ",\"state\":\"pending\""));
        completed = await WaitFor(service, tasks => tasks.All(task => task.State == AgentTaskExecutionStatus.Succeeded && task.Result?.EndsWith(" 2", StringComparison.Ordinal) == true), cancellationToken);
        _ = await Assert.That(string.Join(",", completed.Select(task => task.Result))).IsEqualTo("a done 2,b done 2,c done 2");
        _ = await Assert.That(string.Join(",", fixture.Runtime.Sessions.Identities.Select(identity => identity.Name))).IsEqualTo("a,b,c");
        _ = await Assert.That(string.Join(",", fixture.Runtime.Sessions.ProfileIds.Distinct())).IsEqualTo("agent-task-payload");
        var progress = LastProgress(fixture.Runtime.Parent.SessionId);
        _ = await Assert.That(string.Join(",", progress.RootNodes.Select(node => $"{node.Name}:{node.Status}:{node.AgentSessionId == fixture.Runtime.Sessions.ResolveScope(node.Name).Session.SessionId}")))
            .IsEqualTo("a:Succeeded:True,b:Succeeded:True,c:Succeeded:True");
    }

    [Test]
    [Arguments("pending")]
    [Arguments("succeeded")]
    [Arguments("canceled")]
    public async Task Failure_holds_dependents_until_the_owner_resolves_it(string resolution, CancellationToken cancellationToken)
    {
        var attempts = 0;
        var provider = new AgentTaskReplyProvider((request, _) => Task.FromResult(
            !AgentTaskReplyProvider.IsTaskAgent(request) ? "noted"
            : TaskName(request) == "b" ? Accept("b done")
            : Interlocked.Increment(ref attempts) == 1 ? Halt("broken")
            : Accept("a fixed")));
        var fixture = Fixture(provider, 5, cancellationToken);
        await using var registry = fixture.Runtime.Registry;
        await using var service = fixture.Service;

        Set(fixture, Declare("a", string.Empty), Declare("b", ",\"dependencies\":[\"a\"]"));
        var failed = await WaitFor(service, tasks => tasks[0].State == AgentTaskExecutionStatus.Failed, cancellationToken);
        _ = await Assert.That(States(failed)).IsEqualTo("a:Failed,b:Pending");
        _ = await Assert.That(failed[0].Failure).IsEqualTo("broken");
        _ = await WaitForAdmitted(fixture.Runtime.Parent.SessionId, "AgentTask failed: a\nDescription: Do a\n\nFailure:\nbroken", cancellationToken);

        switch (resolution)
        {
            case "pending":
                Set(fixture, Declare("a", ",\"state\":\"pending\""));
                _ = await WaitFor(service, tasks => tasks.All(task => task.State == AgentTaskExecutionStatus.Succeeded), cancellationToken);
                _ = await Assert.That(DependencyPrompt(provider)).Contains("Result: a fixed");
                _ = await Assert.That(attempts).IsEqualTo(2);
                break;
            case "succeeded":
                Set(fixture, Declare("a", ",\"state\":\"succeeded\",\"result\":\"manual\""));
                _ = await WaitFor(service, tasks => tasks.All(task => task.State == AgentTaskExecutionStatus.Succeeded), cancellationToken);
                _ = await Assert.That(DependencyPrompt(provider)).Contains("Result: manual");
                _ = await Assert.That(attempts).IsEqualTo(1);
                break;
            default:
                Set(fixture, Declare("a", ",\"state\":\"canceled\""));
                _ = await Assert.That(States(service.Snapshot())).IsEqualTo("a:Canceled,b:Canceled");
                _ = await WaitForAdmitted(fixture.Runtime.Parent.SessionId, "All AgentTasks are complete:\n- a: canceled\n- b: canceled", cancellationToken);
                Set(fixture, Declare("a", ",\"state\":\"pending\""));
                _ = await Assert.That(States(service.Snapshot())).IsEqualTo("a:Running,b:Pending");
                _ = await WaitFor(service, tasks => tasks.All(task => task.State == AgentTaskExecutionStatus.Succeeded), cancellationToken);
                _ = await Assert.That(DependencyPrompt(provider)).Contains("Result: a fixed");
                break;
        }

        _ = await Assert.That(string.Join(",", fixture.Runtime.Sessions.Identities.Select(identity => identity.Name))).IsEqualTo("a,b");
    }

    [Test]
    [Arguments("{\"name\":\"a\",\"description\":\"Do a\",\"payload\":\"Work on a\",\"acceptance_criteria\":\"a works\",\"state\":\"canceled\"}", "Canceled:", "the owner canceled this task.")]
    [Arguments("{\"name\":\"a\",\"description\":\"Do a\",\"payload\":\"Work on a\",\"acceptance_criteria\":\"a works\",\"state\":\"succeeded\",\"result\":\"manual\"}", "Succeeded:manual", "the owner marked this task as succeeded.")]
    [Arguments("{\"name\":\"a\",\"description\":\"Do a\",\"payload\":\"Work on a\",\"acceptance_criteria\":\"a works\",\"state\":\"pending\"}", "Succeeded:Work on a", "the owner reset or changed this task; it restarts in this session.")]
    [Arguments("{\"name\":\"a\",\"description\":\"Do a\",\"payload\":\"Work on a differently\",\"acceptance_criteria\":\"a works\",\"state\":\"running\"}", "Succeeded:Work on a differently", "the owner reset or changed this task; it restarts in this session.")]
    public async Task Setting_a_running_task_stops_it_and_records_the_reason_in_its_session(
        string update,
        string expected,
        string reason,
        CancellationToken cancellationToken)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new AgentTaskReplyProvider(async (request, token) =>
        {
            if (!AgentTaskReplyProvider.IsTaskAgent(request))
            {
                return "noted";
            }

            if (arrived.TrySetResult())
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            var prompt = AgentTaskReplyProvider.Prompt(request);
            return Accept(prompt.Contains("Work on a differently", StringComparison.Ordinal) ? "Work on a differently" : "Work on a");
        });
        var fixture = Fixture(provider, 5, cancellationToken);
        await using var registry = fixture.Runtime.Registry;
        await using var service = fixture.Service;

        Set(fixture, Declare("a", string.Empty));
        await arrived.Task.WaitAsync(cancellationToken);
        fixture.Service.SetTasks(Tasks(update), fixture.Runtime.Selection, new HistoryForkBoundary.AfterCompletedHistory());

        var settled = await WaitFor(service, tasks => tasks[0].State is AgentTaskExecutionStatus.Succeeded or AgentTaskExecutionStatus.Canceled, cancellationToken);
        _ = await Assert.That($"{settled[0].State}:{settled[0].Result}").IsEqualTo(expected);
        var notice = $"Your AgentTask turn was stopped: {reason}";
        _ = await WaitForAdmitted(fixture.Runtime.Sessions.ResolveScope("a").Session.SessionId, notice, cancellationToken);
        _ = await Assert.That(fixture.Runtime.Sessions.Identities).Count().IsEqualTo(1);
        var taskRequests = provider.Requests.Where(AgentTaskReplyProvider.IsTaskAgent).ToArray();
        if (expected.StartsWith("Succeeded:Work", StringComparison.Ordinal))
        {
            _ = await Assert.That(taskRequests).Count().IsEqualTo(2);
            _ = await Assert.That(AgentTaskReplyProvider.Conversation(taskRequests[1])).Contains(notice);
        }
        else
        {
            _ = await Assert.That(taskRequests).Count().IsEqualTo(1);
        }
    }

    [Test]
    [Arguments("RETRY|ACCEPT", "Succeeded:a done:", 2, "Work again")]
    [Arguments("RETRY|RETRY|RETRY", "Failed:a 3:rejected after 3 attempts: fix 3", 3, "Retry feedback:\n- fix 1\n- fix 2")]
    [Arguments("HALT", "Failed:a 1:nope", 1, "")]
    [Arguments("not json|ACCEPT", "Succeeded:a done:", 2, "Your previous response was not accepted")]
    [Arguments("not json|still not json", "Failed::leaf response invalid: The response does not end with a JSON object or array envelope.", 2, "Your previous response was not accepted")]
    public async Task Retries_a_rejected_attempt_internally_up_to_the_attempt_limit(
        string replies,
        string expected,
        int requests,
        string lastPrompt,
        CancellationToken cancellationToken)
    {
        var replyQueue = new ConcurrentQueue<string>(replies.Split('|'));
        var attempt = 0;
        var provider = new AgentTaskReplyProvider((request, _) =>
        {
            if (!AgentTaskReplyProvider.IsTaskAgent(request))
            {
                return Task.FromResult("noted");
            }

            var number = Interlocked.Increment(ref attempt);
            return Task.FromResult(replyQueue.TryDequeue(out var reply) ? reply switch
            {
                "ACCEPT" => Accept("a done"),
                "HALT" => Halt("nope"),
                "RETRY" => $$"""{"result":"a {{number}}","verdict":"reject_and_retry","feedback":"fix {{number}}","payload":"Work again"}""",
                _ => reply,
            } : throw new InvalidOperationException("No reply remains."));
        });
        var fixture = Fixture(provider, 3, cancellationToken);
        await using var registry = fixture.Runtime.Registry;
        await using var service = fixture.Service;

        Set(fixture, Declare("a", string.Empty));

        var settled = await WaitFor(service, tasks => tasks[0].State != AgentTaskExecutionStatus.Running, cancellationToken);
        _ = await Assert.That($"{settled[0].State}:{settled[0].Result}:{settled[0].Failure}").IsEqualTo(expected);
        var taskRequests = provider.Requests.Where(AgentTaskReplyProvider.IsTaskAgent).ToArray();
        _ = await Assert.That(taskRequests).Count().IsEqualTo(requests);
        _ = await Assert.That(AgentTaskReplyProvider.Prompt(taskRequests[^1])).Contains(lastPrompt);
        _ = await Assert.That(fixture.Runtime.Sessions.Identities).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Composite_task_runs_its_children_in_its_own_agent_task_service(CancellationToken cancellationToken)
    {
        RuntimeContext? runtime = null;
        var provider = new AgentTaskReplyProvider(async (request, token) =>
        {
            if (!AgentTaskReplyProvider.IsTaskAgent(request))
            {
                return "noted";
            }

            var name = TaskName(request);
            if (name != "parent")
            {
                return Accept($"{name} done");
            }

            var children = (runtime ?? throw new InvalidOperationException("The runtime is not ready.")).Sessions.ResolveScope("parent").GetService<IAgentTaskService>();
            _ = await WaitFor(children, tasks => tasks.All(task => task.State == AgentTaskExecutionStatus.Succeeded), token);
            return Accept("parent done");
        });
        var fixture = Fixture(provider, 5, cancellationToken);
        runtime = fixture.Runtime;
        await using var registry = fixture.Runtime.Registry;
        await using var service = fixture.Service;

        Set(fixture, Declare("parent", string.Empty).Replace("\"Work on parent\"", "[" + Declare("x", string.Empty) + "," + Declare("y", ",\"dependencies\":[\"x\"]") + "]", StringComparison.Ordinal));

        var completed = await WaitFor(service, tasks => tasks[0].State == AgentTaskExecutionStatus.Succeeded, cancellationToken);
        _ = await Assert.That(completed[0].Result).IsEqualTo("parent done");
        var composite = fixture.Runtime.Sessions.ResolveScope("parent");
        _ = await Assert.That(string.Join(",", composite.GetService<IAgentTaskService>().Snapshot().Select(task => $"{task.Name}:{task.State}:{task.Result}")))
            .IsEqualTo("x:Succeeded:x done,y:Succeeded:y done");
        _ = await Assert.That(string.Join(",", fixture.Runtime.Sessions.Identities.Select(identity => $"{identity.Name}:{identity.ParentSessionId == composite.Session.SessionId}")))
            .IsEqualTo("parent:False,x:True,y:True");
        var compositeConversation = AgentTaskReplyProvider.Conversation(provider.Requests.First(request => AgentTaskReplyProvider.IsTaskAgent(request) && TaskName(request) == "parent"));
        _ = await Assert.That(compositeConversation).Contains("AgentTask role: composite owner")
            .And.Contains("\n[x] Do x\n[y] Do y");
        _ = await Assert.That(LastProgress(fixture.Runtime.Parent.SessionId).RootNodes.Single().AgentSessionId).IsEqualTo(composite.Session.SessionId);
    }

    [Test]
    public async Task Shutdown_force_stops_running_tasks_and_cancels_unresolved_ones(CancellationToken cancellationToken)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new AgentTaskReplyProvider(async (request, token) =>
        {
            if (!AgentTaskReplyProvider.IsTaskAgent(request))
            {
                return "noted";
            }

            if (TaskName(request) == "c")
            {
                return Halt("broken");
            }

            _ = arrived.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Accept("unreachable");
        });
        var fixture = Fixture(provider, 5, cancellationToken);
        await using var registry = fixture.Runtime.Registry;
        var service = fixture.Service;
        Set(fixture, Declare("a", string.Empty), Declare("b", ",\"dependencies\":[\"a\"]"), Declare("c", string.Empty));
        await arrived.Task.WaitAsync(cancellationToken);
        _ = await WaitFor(service, tasks => tasks[2].State == AgentTaskExecutionStatus.Failed, cancellationToken);

        var blocked = new AgentTaskActiveWorkBlocker(service, TestModels.PromptTemplates).Observe();
        _ = await Assert.That(blocked?.WorkSection).Contains("- a [running] (name: Do a)\n- b [pending] (name: Do b)\n- c [failed] (name: Do c)");
        var status = await new AgentTaskStatusProvider(service, TestModels.PromptTemplates).Observe(
            new StatusQuery(fixture.Runtime.Parent.SessionId, string.Empty, string.Empty, "profile", "model"), cancellationToken);
        _ = await Assert.That(status.Text).Contains("- task: a (running, description: Do a)");

        await service.DisposeAsync();

        _ = await Assert.That(States(service.Snapshot())).IsEqualTo("a:Canceled,b:Canceled,c:Canceled");
        _ = await Assert.That(new AgentTaskActiveWorkBlocker(service, TestModels.PromptTemplates).Observe()).IsNull();
        _ = await Assert.That(() => Set(fixture, Declare("d", string.Empty))).Throws<InvalidOperationException>();
        _ = await WaitForAdmitted(
            fixture.Runtime.Sessions.ResolveScope("a").Session.SessionId,
            "Your AgentTask turn was stopped: the agent session is shutting down.",
            cancellationToken);
        _ = await Assert.That(string.Join(",", LastProgress(fixture.Runtime.Parent.SessionId).RootNodes.Select(node => node.Status)))
            .IsEqualTo("Canceled,Canceled,Canceled");
    }

    [Test]
    public async Task Reuses_manual_agent_configuration_and_delivery_before_resolving_requested_model(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskReplyProvider((request, _) => Task.FromResult(
            AgentTaskReplyProvider.Prompt(request).Contains("Task: existing-agent", StringComparison.Ordinal)
                ? Accept("task result")
                : "manual history"));
        var fixture = Fixture(provider, 5, cancellationToken);
        await using var registry = fixture.Runtime.Registry;
        await using var service = fixture.Service;
        var runtime = fixture.Runtime;
        var manualScope = runtime.ParentScope.AgentSpawner.SpawnScope(new AgentLaunchRequest(
            runtime.Parent,
            runtime.Selection,
            "worker",
            runtime.Selection.RequestedModel,
            "existing-agent",
            "manual scope",
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic));
        using var turns = _broker.Subscribe();
        _ = await manualScope.Session.SendAndWaitForResult("manual prompt", cancellationToken);
        _ = await turns.TurnEnding(runtime.Parent.SessionId, cancellationToken);
        var originalSelection = manualScope.Session.CurrentSelection();

        Set(fixture, Declare("existing-agent", ",\"model\":\"missing-provider/missing-model\""));
        var completed = await WaitFor(service, tasks => tasks.Single().State == AgentTaskExecutionStatus.Succeeded, cancellationToken);
        _ = await WaitForAdmitted(runtime.Parent.SessionId, "task result", cancellationToken);

        _ = await Assert.That(completed.Single().Result).IsEqualTo("task result");
        _ = await Assert.That(runtime.Sessions.Identities).Count().IsEqualTo(1);
        _ = await Assert.That(runtime.Sessions.ProfileIds.Single()).IsEqualTo("worker");
        _ = await Assert.That(manualScope.Session.CurrentSelection()).IsEqualTo(originalSelection);
        _ = await Assert.That(manualScope.ParentScope.DeliveryPolicy).IsEqualTo(AgentCompletionDeliveryPolicy.Automatic);
        var taskRequest = provider.Requests.Single(request => AgentTaskReplyProvider.Prompt(request).Contains("Task: existing-agent", StringComparison.Ordinal));
        _ = await Assert.That(taskRequest.Messages.Select(message => message.Content)).Contains("manual history");
        _ = await Assert.That(() => Set(fixture, Declare("new-agent", ",\"model\":\"missing-provider/missing-model\"")))
            .Throws<LLMProviderException>().WithMessage("provider: unknown provider \"missing-provider\"");
        _ = await Assert.That(runtime.Sessions.Identities).Count().IsEqualTo(1);
    }

    private static string Accept(string result) => $$"""{"result":"{{result}}","verdict":"accept","evidence":"checked"}""";

    private static string Halt(string feedback) => $$"""{"result":"a 1","verdict":"reject_and_halt","feedback":"{{feedback}}"}""";

    private static string Declare(string name, string extra) =>
        $$"""{"name":"{{name}}","description":"Do {{name}}","payload":"Work on {{name}}","acceptance_criteria":"{{name}} works"{{extra}}}""";

    private static IReadOnlyList<AgentTask> Tasks(params string[] declarations) =>
        AgentTaskParser.ParseTaskSet("[" + string.Join(",", declarations) + "]");

    private static void Set((RuntimeContext Runtime, AgentTaskService Service) fixture, params string[] declarations) =>
        fixture.Service.SetTasks(Tasks(declarations), fixture.Runtime.Selection, new HistoryForkBoundary.AfterCompletedHistory());

    private static string States(IReadOnlyList<AgentTask> tasks) =>
        string.Join(",", tasks.Select(task => $"{task.Name}:{task.State}"));

    private static string TaskName(LLMRequest request)
    {
        var conversation = AgentTaskReplyProvider.Conversation(request);
        var start = conversation.IndexOf("Task: ", StringComparison.Ordinal) + "Task: ".Length;
        return conversation[start..conversation.IndexOf('\n', start)];
    }

    private static string DependencyPrompt(AgentTaskReplyProvider provider) =>
        AgentTaskReplyProvider.Prompt(provider.Requests.Last(request => AgentTaskReplyProvider.IsTaskAgent(request) && TaskName(request) == "b"));

    private static async Task<IReadOnlyList<AgentTask>> WaitFor(
        IAgentTaskService service,
        Func<IReadOnlyList<AgentTask>, bool> condition,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var tasks = service.Snapshot();
            if (condition(tasks))
            {
                return tasks;
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    private async Task<string> WaitForAdmitted(string agentSessionId, string expected, CancellationToken cancellationToken)
    {
        while (true)
        {
            var admitted = string.Join("\n---\n", _repository.Replay()
                .Where(published => published.AgentSessionId == agentSessionId && published.PayloadCase == Event.PayloadOneofCase.InputAdmitted)
                .Select(published => published.InputAdmitted.Content));
            if (admitted.Contains(expected, StringComparison.Ordinal))
            {
                return admitted;
            }

            await Task.Delay(10, cancellationToken);
        }
    }

    private AgentTaskProgressSnapshot LastProgress(string agentSessionId) =>
        _repository.Replay()
            .Last(published => published.AgentSessionId == agentSessionId && published.PayloadCase == Event.PayloadOneofCase.AgentTaskProgressSnapshot)
            .AgentTaskProgressSnapshot;

    private (RuntimeContext Runtime, AgentTaskService Service) Fixture(ILLMProvider provider, int maximumAttempts, CancellationToken cancellationToken)
    {
        var runtime = Runtime(provider, cancellationToken);
        return (runtime, new AgentTaskService(
            runtime.ParentScope,
            runtime.Parent.SessionId,
            runtime.Router,
            new AgentTaskConfig(maximumAttempts, 1, false, TestModels.PromptTemplates),
            new AgentTaskNotifier(runtime.ParentScope, new ToolOutputBlobStore(Path.GetTempPath()), TestDiagnosticLog.Instance),
            _broker,
            _repository,
            TestDiagnosticLog.Instance,
            cancellationToken));
    }

    private RuntimeContext Runtime(ILLMProvider provider, CancellationToken cancellationToken)
    {
        var model = new LLMModel("model", provider.Id);
        var providers = new ProviderRegistry(
            [provider],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal) { [provider.Id] = [model] });
        var router = new ModelRouter(providers, new ModelRouting(new ModelAliasCatalog(providers, []), $"{provider.Id}/model"));
        var sessions = new AgentTaskTestSessionFactory(router);
        var registry = TestModels.Registry(
            sessions,
            _broker,
            _repository,
            new TestProfileFixture().Registry,
            TestModels.PromptTemplates,
            cancellationToken);
        _registries.Add(registry);
        var identity = AgentIdentity.Main("agent-task-parent", "parent", TestModels.PromptTemplates);
        using var dependencies = TestModels.Dependencies(identity, _broker, _repository, cancellationToken);
        using var parentScope = TestAgentSessionScope.Build(identity, AgentSessionParentLink.Root(), registry, TestModels.PromptTemplates, (sessionParentScope, _, children, childQuestions) => new AgentSession(
            identity,
            sessionParentScope,
            new ModelSelector($"{provider.Id}/model"),
            router,
            _broker,
            _repository,
            [],
            TestModels.MaterializePrompt(identity, ".", "."),
            new ToolOutputBlobStore(Path.GetTempPath()),
            new AgentOutputFile(Path.GetTempPath()),
            TestModels.CompactionGroupBlobs(),
            new Parrot.Context.Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates),
            new ProviderSessions(TestDiagnosticLog.Instance, "agent-test", null),
            new Parrot.Context.ContextCadence(),
            TestModels.PromptTemplates,
            childQuestions,
            dependencies.ExitReminder,
            dependencies.Profile,
            new TestCompletionCallbacksFixture(childQuestions, dependencies.ActiveWorkReminder, dependencies.ExitReminder, _repository, _broker).Callbacks,
            new SecurityProfileTestFixture(SecurityProfile.Compose(false, [], [], [])).Security,
            dependencies.Status,
            new AgentSessionActivity(TimeProvider.System),
            TestDiagnosticLog.Instance,
            cancellationToken));
        registry.RegisterRootScope(parentScope);
        var parent = parentScope.Session;
        var selected = parent.CurrentSelection();
        var selection = new AgentTurnSelection(
            selected.RequestedModel,
            router.Resolve(selected.RequestedModel.Value),
            selected.Mode,
            selected.SecurityProfile);
        return new RuntimeContext(router, sessions, registry, parentScope, parent, selection);
    }

    private sealed record RuntimeContext(
        IModelRouter Router,
        AgentTaskTestSessionFactory Sessions,
        IAgentRegistry Registry,
        IAgentSessionScope ParentScope,
        IAgentSession Parent,
        AgentTurnSelection Selection);

    private sealed class DiagnosticChildFactory(IAgentSessionScope scope, bool fail) : IAgentSessionFactory
    {
        public IEventRepository PrepareHistory(AgentIdentity identity, IEventRepository repository) =>
            repository;

        public IAgentSessionScope Create(
            AgentIdentity identity,
            AgentSessionParentLink parentLink,
            ModelSelector model,
            IEventBroker eventBroker,
            IEventRepository eventRepository,
            IMode mode,
            SecurityProfile securityProfile,
            IAgentRegistry registry,
            CancellationToken lifetime) =>
            fail ? throw new InvalidOperationException("secret-exception") : scope;
    }
}
