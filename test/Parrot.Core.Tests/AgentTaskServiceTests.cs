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
                dependencies.Profile.SecurityProfile,
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
        var fixture = Fixture(provider, 5, AgentTaskForkHistoryMode.Empty, cancellationToken);
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
        _ = await Assert.That(completed.All(task => task.Hidden)).IsTrue();
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
        var fixture = Fixture(provider, 5, AgentTaskForkHistoryMode.Empty, cancellationToken);
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
        var fixture = Fixture(provider, 5, AgentTaskForkHistoryMode.Empty, cancellationToken);
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
        var fixture = Fixture(provider, 3, AgentTaskForkHistoryMode.Empty, cancellationToken);
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
    public async Task Composite_task_registers_delivered_children_only_when_its_agent_declares_them(CancellationToken cancellationToken)
    {
        RuntimeContext? runtime = null;
        var provider = new AgentTaskReplyProvider(async (request, token) =>
        {
            if (!AgentTaskReplyProvider.IsTaskAgent(request))
            {
                return "noted";
            }

            if (!AgentTaskReplyProvider.Prompt(request).Contains("Task: ", StringComparison.Ordinal))
            {
                return Accept("parent done");
            }

            var name = TaskName(request);
            if (name != "parent")
            {
                if (name == "y")
                {
                    var owner = (runtime ?? throw new InvalidOperationException("The runtime is not ready.")).Sessions.ResolveScope("parent").GetService<IAgentTaskService>();
                    _ = await Assert.That(owner.Snapshot().Single(task => task.Name == "x").State).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
                    _ = await Assert.That(AgentTaskReplyProvider.Prompt(request)).Contains("Result: x done");
                }

                return Accept($"{name} done");
            }

            var context = runtime ?? throw new InvalidOperationException("The runtime is not ready.");
            var children = context.Sessions.ResolveScope("parent").GetService<IAgentTaskService>();
            _ = await Assert.That(children.Snapshot()).IsEmpty();
            _ = await Assert.That(context.Sessions.Identities.Select(identity => identity.Name)).IsEquivalentTo(["parent"]);
            children.SetTasks(PromptTasks(request), context.Selection, new HistoryForkBoundary.AfterCompletedHistory());
            _ = await WaitFor(children, tasks => tasks.Count == 2 && tasks.All(task => task.State == AgentTaskExecutionStatus.Succeeded), token);
            return Accept("parent done");
        });
        var fixture = Fixture(provider, 5, AgentTaskForkHistoryMode.Empty, cancellationToken);
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
            .And.Contains("use set_agent_tasks to declare and manage this work");
        var delivered = PromptTasks(provider.Requests.First(request => AgentTaskReplyProvider.IsTaskAgent(request) && TaskName(request) == "parent"));
        _ = await Assert.That(delivered.Select(task => task.Name)).IsEquivalentTo(["x", "y"]);
        _ = await Assert.That(delivered[1].Dependencies).IsEquivalentTo(["x"]);
        _ = await Assert.That(provider.Requests.Where(request => AgentTaskReplyProvider.IsTaskAgent(request) && AgentTaskReplyProvider.Prompt(request).Contains("Task: ", StringComparison.Ordinal)).Select(TaskName)).IsEquivalentTo(["parent", "x", "y"]);
        _ = await Assert.That(LastProgress(fixture.Runtime.Parent.SessionId).RootNodes.Single().AgentSessionId).IsEqualTo(composite.Session.SessionId);
    }

    [Test]
    public async Task Composite_prompt_preserves_complete_recursive_definitions_without_registering_them(CancellationToken cancellationToken)
    {
        const string definition = """
            {"name":"parent","description":"Own work","payload":[
              {"name":"source","description":"Quote \" and braces {}","payload":"Line one\nLine two \"{}\"","acceptance_criteria":"Source evidence","model":"chosen-model","hidden":true,"state":"succeeded","result":"Source result"},
              {"name":"nested","description":"Nested work","dependencies":["source"],"payload":[
                {"name":"failed","description":"Failed work","payload":"Fix it","acceptance_criteria":"Failure evidence","state":"failed","result":"Partial result","failure":"Failure reason"}
              ],"acceptance_criteria":"Nested evidence","state":"canceled"}
            ],"acceptance_criteria":"Done"}
            """;
        RuntimeContext? runtime = null;
        var provider = new AgentTaskReplyProvider(async (request, token) =>
        {
            if (!AgentTaskReplyProvider.IsTaskAgent(request))
            {
                return "noted";
            }

            var context = runtime ?? throw new InvalidOperationException("The runtime is not ready.");
            var scope = context.Sessions.ResolveScope("parent");
            _ = await Assert.That(scope.GetService<IAgentTaskService>().Snapshot()).IsEmpty();
            _ = await Assert.That(context.Sessions.Identities).Count().IsEqualTo(1);
            var expected = Tasks(definition).Single().Payload.Tasks ?? throw new InvalidOperationException("Missing inner tasks.");
            var delivered = PromptTasks(request);
            _ = await Assert.That(delivered.Count).IsEqualTo(2);
            _ = await Assert.That(delivered.Zip(expected).All(pair => pair.First.HasSameDefinition(pair.Second)
                && pair.First.HasSameVisibility(pair.Second))).IsTrue();
            _ = await Assert.That(delivered[0].Payload.Instruction).IsEqualTo("Line one\nLine two \"{}\"");
            _ = await Assert.That(delivered[0].Model).IsEqualTo("chosen-model");
            _ = await Assert.That(delivered[0].State).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
            _ = await Assert.That(delivered[0].Result).IsEqualTo("Source result");
            _ = await Assert.That(delivered[1].State).IsEqualTo(AgentTaskExecutionStatus.Canceled);
            var nested = delivered[1].Payload.Tasks ?? throw new InvalidOperationException("Missing nested tasks.");
            _ = await Assert.That(nested.Single().State).IsEqualTo(AgentTaskExecutionStatus.Failed);
            _ = await Assert.That(nested.Single().Result).IsEqualTo("Partial result");
            _ = await Assert.That(nested.Single().Failure).IsEqualTo("Failure reason");
            _ = await Assert.That(AgentTaskReplyProvider.Prompt(request)).DoesNotContain("agent_name");
            return Accept("No registration required");
        });
        var fixture = Fixture(provider, 5, AgentTaskForkHistoryMode.Empty, cancellationToken);
        runtime = fixture.Runtime;
        await using var registry = fixture.Runtime.Registry;
        await using var service = fixture.Service;
        Set(fixture, definition);
        var completed = await WaitFor(service, tasks => tasks.Single().State == AgentTaskExecutionStatus.Succeeded, cancellationToken);
        _ = await Assert.That(completed.Single().Result).IsEqualTo("No registration required");
        _ = await Assert.That(fixture.Runtime.Sessions.Identities).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Composite_retries_and_definition_restarts_preserve_independently_declared_child_graph(CancellationToken cancellationToken)
    {
        RuntimeContext? runtime = null;
        var attempts = 0;
        var provider = new AgentTaskReplyProvider(async (request, token) =>
        {
            if (!AgentTaskReplyProvider.IsTaskAgent(request))
            {
                return "noted";
            }

            if (!AgentTaskReplyProvider.Prompt(request).Contains("AgentTask role: composite owner", StringComparison.Ordinal))
            {
                return Volatile.Read(ref attempts) == 1
                    ? """{"result":"Try again","verdict":"reject_and_retry","feedback":"Retry without touching my graph"}"""
                    : Accept("parent done");
            }

            var context = runtime ?? throw new InvalidOperationException("The runtime is not ready.");
            var children = context.Sessions.ResolveScope("parent").GetService<IAgentTaskService>();
            var attempt = Interlocked.Increment(ref attempts);
            if (attempt == 1)
            {
                _ = await Assert.That(children.Snapshot()).IsEmpty();
                children.SetTasks(Tasks(Declare("shared", ",\"state\":\"canceled\"").Replace("Work on shared", "Child-owned edit", StringComparison.Ordinal), Declare("extra", ",\"state\":\"canceled\"")), context.Selection, new HistoryForkBoundary.AfterCompletedHistory());
                return """{"result":"Try again","verdict":"reject_and_retry","feedback":"Retry without touching my graph"}""";
            }

            if (attempt == 2)
            {
                _ = await Assert.That(AgentTaskReplyProvider.Prompt(request)).Contains("Retry without touching my graph");
            }

            _ = await Assert.That(States(children.Snapshot())).IsEqualTo("shared:Canceled,extra:Canceled");
            _ = await Assert.That(children.Snapshot()[0].Payload.Instruction).IsEqualTo("Child-owned edit");
            _ = await Assert.That(context.Sessions.Identities).Count().IsEqualTo(1);
            _ = await Assert.That(PromptTasks(request).Select(task => task.Name)).IsEquivalentTo(["shared", "missing"]);
            return Accept("parent done");
        });
        var fixture = Fixture(provider, 5, AgentTaskForkHistoryMode.Empty, cancellationToken);
        runtime = fixture.Runtime;
        await using var registry = fixture.Runtime.Registry;
        await using var service = fixture.Service;
        var declaration = Declare("parent", string.Empty).Replace("\"Work on parent\"", "[" + Declare("shared", string.Empty) + "," + Declare("missing", string.Empty) + "]", StringComparison.Ordinal);
        Set(fixture, declaration);
        _ = await WaitFor(service, tasks => tasks.Single().State == AgentTaskExecutionStatus.Succeeded, cancellationToken);
        Set(fixture, declaration.Replace("parent works", "Updated acceptance", StringComparison.Ordinal));
        _ = await WaitFor(service, tasks => tasks.Single().State == AgentTaskExecutionStatus.Succeeded, cancellationToken);
        _ = await Assert.That(attempts).IsEqualTo(3);
        _ = await Assert.That(fixture.Runtime.Sessions.Identities).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Composite_nested_visibility_changes_remain_local_without_restarting_execution(CancellationToken cancellationToken)
    {
        RuntimeContext? runtime = null;
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var provider = new AgentTaskReplyProvider(async (request, token) =>
        {
            if (!AgentTaskReplyProvider.IsTaskAgent(request))
            {
                return "noted";
            }

            if (!AgentTaskReplyProvider.Prompt(request).Contains("AgentTask role: composite owner", StringComparison.Ordinal))
            {
                return Accept("parent done");
            }

            _ = Interlocked.Increment(ref requests);
            var context = runtime ?? throw new InvalidOperationException("The runtime is not ready.");
            var children = context.Sessions.ResolveScope("parent").GetService<IAgentTaskService>();
            _ = await Assert.That(children.Snapshot()).IsEmpty();
            children.SetTasks(Tasks(Declare("shared", ",\"state\":\"canceled\"")), context.Selection, new HistoryForkBoundary.AfterCompletedHistory());
            arrived.SetResult();
            await release.Task.WaitAsync(token);
            return Accept("parent done");
        });
        var fixture = Fixture(provider, 5, AgentTaskForkHistoryMode.Empty, cancellationToken);
        runtime = fixture.Runtime;
        await using var registry = fixture.Runtime.Registry;
        await using var service = fixture.Service;
        var declaration = Declare("parent", string.Empty).Replace("\"Work on parent\"", "[" + Declare("shared", ",\"state\":\"canceled\"") + "]", StringComparison.Ordinal);
        Set(fixture, declaration);
        await arrived.Task.WaitAsync(cancellationToken);
        var scope = fixture.Runtime.Sessions.ResolveScope("parent");
        var child = scope.GetService<IAgentTaskService>();
        var original = service.Snapshot().Single();
        var hiddenPayload = AgentTaskPayload.FromTasks([(original.Payload.Tasks ?? throw new InvalidOperationException("Missing tasks.")).Single() with { Hidden = true }]);
        service.SetTasks([original with { State = AgentTaskExecutionStatus.Running, Payload = hiddenPayload }], fixture.Runtime.Selection, new HistoryForkBoundary.AfterCompletedHistory());
        _ = await Assert.That((service.Snapshot().Single().Payload.Tasks ?? throw new InvalidOperationException("Missing tasks.")).Single().Hidden).IsTrue();
        _ = await Assert.That(child.Snapshot().Single().Hidden).IsFalse();
        var before = service.Snapshot();
        service.ApplyVisibilityChanges(before, [before.Single() with { Payload = original.Payload }]);
        _ = await Assert.That((service.Snapshot().Single().Payload.Tasks ?? throw new InvalidOperationException("Missing tasks.")).Single().Hidden).IsFalse();
        before = service.Snapshot();
        service.ApplyVisibilityChanges(before, [before.Single() with { Payload = hiddenPayload }]);
        _ = await Assert.That((service.Snapshot().Single().Payload.Tasks ?? throw new InvalidOperationException("Missing tasks.")).Single().Hidden).IsTrue();
        _ = await Assert.That(child.Snapshot().Single().Hidden).IsFalse();
        _ = await Assert.That(service.Snapshot().Single().State).IsEqualTo(AgentTaskExecutionStatus.Running);
        _ = await Assert.That(service.CaptureDetail("parent")?.AgentName).IsEqualTo(scope.Session.Name);
        _ = await Assert.That(requests).IsEqualTo(1);
        release.SetResult();
        _ = await WaitFor(service, tasks => tasks.Single().State == AgentTaskExecutionStatus.Succeeded, cancellationToken);
        _ = await Assert.That(requests).IsEqualTo(1);
    }

    [Test]
    public async Task Unspawned_tasks_capture_detail_and_apply_visibility_without_creating_workers(CancellationToken cancellationToken)
    {
        var fixture = Fixture(new AgentTaskReplyProvider((_, _) => Task.FromResult("noted")), 5, AgentTaskForkHistoryMode.Empty, cancellationToken);
        await using var registry = fixture.Runtime.Registry;
        await using var service = fixture.Service;
        var child = Declare("child", string.Empty);
        var blocked = Declare("blocked", ",\"dependencies\":[\"dependency\"]")
            .Replace("\"Work on blocked\"", "[" + child + "]", StringComparison.Ordinal);
        Set(fixture, Declare("dependency", ",\"state\":\"failed\""), blocked);
        var detail = service.CaptureDetail("blocked") ?? throw new InvalidOperationException("The task is missing.");
        _ = await Assert.That(detail.AgentName).IsNull();
        _ = await Assert.That(detail.Task.State).IsEqualTo(AgentTaskExecutionStatus.Pending);
        _ = await Assert.That(LastProgress(fixture.Runtime.Parent.SessionId).RootNodes.All(node => node.AgentSessionId.Length == 0)).IsTrue();

        var before = service.Snapshot();
        var incoming = AgentTaskParser.ParseTaskSet("[" + blocked.Replace(child, Declare("child", ",\"hidden\":true"), StringComparison.Ordinal) + "]");
        service.ApplyVisibilityChanges(before, incoming);
        var children = service.Snapshot()[1].Payload.Tasks ?? throw new InvalidOperationException("The child declarations are missing.");
        _ = await Assert.That(children[0].Hidden).IsTrue();
        _ = await Assert.That(service.CaptureDetail("blocked")?.AgentName).IsNull();
        _ = await Assert.That(LastProgress(fixture.Runtime.Parent.SessionId).RootNodes.All(node => node.AgentSessionId.Length == 0)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Visibility_updates_preserve_running_execution_and_completion(bool hiddenAtCompletion, CancellationToken cancellationToken)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new AgentTaskReplyProvider(async (request, token) =>
        {
            if (!AgentTaskReplyProvider.IsTaskAgent(request))
            {
                return "noted";
            }

            _ = arrived.TrySetResult();
            await release.Task.WaitAsync(token);
            return Accept("a done");
        });
        var fixture = Fixture(provider, 5, AgentTaskForkHistoryMode.Empty, cancellationToken);
        await using var registry = fixture.Runtime.Registry;
        await using var service = fixture.Service;
        Set(fixture, Declare("a", string.Empty), Declare("blocker", ",\"state\":\"canceled\""));
        await arrived.Task.WaitAsync(cancellationToken);
        var scope = fixture.Runtime.Sessions.ResolveScope("a");

        Set(fixture, Declare("a", ",\"state\":\"running\",\"hidden\":true"));
        _ = await Assert.That(service.Snapshot()[0].Hidden).IsTrue();
        _ = await Assert.That(LastProgress(fixture.Runtime.Parent.SessionId).RootNodes[0].Hidden).IsTrue();
        _ = await Assert.That(service.CaptureDetail("a")?.AgentName).IsEqualTo(scope.Session.Name);
        _ = await Assert.That(service.CaptureDetail("missing")).IsNull();
        _ = await Assert.That(new AgentTaskActiveWorkBlocker(service, TestModels.PromptTemplates).Observe()?.WorkSection).Contains("- a [running]");

        Set(fixture, Declare("a", ",\"state\":\"running\",\"hidden\":false"));
        _ = await Assert.That(service.Snapshot()[0].Hidden).IsFalse();
        Set(fixture, Declare("a", $",\"state\":\"running\",\"hidden\":{(hiddenAtCompletion ? "true" : "false")}"));
        var beforeCompletion = service.Snapshot();
        release.SetResult();
        var settled = await WaitFor(service, tasks => tasks[0].State == AgentTaskExecutionStatus.Succeeded, cancellationToken);
        _ = await Assert.That(settled[0].Hidden).IsEqualTo(hiddenAtCompletion);
        _ = await Assert.That(settled[1].Hidden).IsFalse();
        service.ApplyVisibilityChanges(beforeCompletion, [beforeCompletion[0] with { Hidden = !hiddenAtCompletion }]);
        _ = await Assert.That(service.Snapshot()[0].Hidden).IsEqualTo(!hiddenAtCompletion);
        _ = await Assert.That(service.Snapshot()[0].State).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        _ = await Assert.That(service.Snapshot()[0].Result).IsEqualTo("a done");
        _ = await Assert.That(ReferenceEquals(scope, fixture.Runtime.Sessions.ResolveScope("a"))).IsTrue();
        _ = await Assert.That(provider.Requests.Count(AgentTaskReplyProvider.IsTaskAgent)).IsEqualTo(1);
    }

    [Test]
    [Arguments("canceled")]
    [Arguments("failed")]
    [Arguments("pending")]
    [Arguments("running")]
    public async Task Automatic_hiding_requires_every_task_to_succeed(string state, CancellationToken cancellationToken)
    {
        var provider = new AgentTaskReplyProvider(async (request, token) =>
        {
            if (!AgentTaskReplyProvider.IsTaskAgent(request))
            {
                return "noted";
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Accept("unreachable");
        });
        var fixture = Fixture(provider, 5, AgentTaskForkHistoryMode.Empty, cancellationToken);
        await using var registry = fixture.Runtime.Registry;
        await using var service = fixture.Service;
        _ = await Assert.That(service.Snapshot()).Count().IsEqualTo(0);
        Set(fixture, Declare("a", ",\"state\":\"succeeded\""), Declare("b", $",\"state\":\"{state}\""));
        _ = await Assert.That(service.Snapshot().Any(task => task.Hidden)).IsFalse();
        _ = await Assert.That(LastProgress(fixture.Runtime.Parent.SessionId).RootNodes.Any(node => node.Hidden)).IsFalse();
    }

    [Test]
    public async Task All_success_hides_once_per_transition_and_allows_explicit_unhide(CancellationToken cancellationToken)
    {
        var fixture = Fixture(new AgentTaskReplyProvider((_, _) => Task.FromResult("noted")), 5, AgentTaskForkHistoryMode.Empty, cancellationToken);
        await using var registry = fixture.Runtime.Registry;
        await using var service = fixture.Service;
        Set(fixture, Declare("a", ",\"state\":\"succeeded\""), Declare("b", ",\"state\":\"succeeded\""));
        _ = await Assert.That(service.Snapshot().All(task => task.Hidden)).IsTrue();
        _ = await Assert.That(LastProgress(fixture.Runtime.Parent.SessionId).RootNodes.All(node => node.Hidden)).IsTrue();
        Set(fixture, Declare("a", ",\"state\":\"succeeded\",\"hidden\":false"));
        _ = await Assert.That(service.Snapshot()[0].Hidden).IsFalse();
        _ = await Assert.That(service.Snapshot()[1].Hidden).IsTrue();
        Set(fixture, Declare("b", ",\"state\":\"succeeded\",\"hidden\":false"));
        _ = await Assert.That(service.Snapshot().Any(task => task.Hidden)).IsFalse();
        Set(fixture, Declare("b", ",\"state\":\"failed\""));
        _ = await Assert.That(service.Snapshot().Any(task => task.Hidden)).IsFalse();
        Set(fixture, Declare("b", ",\"state\":\"succeeded\""));
        _ = await Assert.That(service.Snapshot().All(task => task.Hidden)).IsTrue();
    }

    [Test]
    public async Task Restarted_hidden_task_runs_on_retained_agent_and_hides_again_on_success(CancellationToken cancellationToken)
    {
        var attempts = 0;
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new AgentTaskReplyProvider(async (request, token) =>
        {
            if (!AgentTaskReplyProvider.IsTaskAgent(request))
            {
                return "noted";
            }

            if (Interlocked.Increment(ref attempts) == 1)
            {
                return Accept("initial done");
            }

            _ = arrived.TrySetResult();
            await release.Task.WaitAsync(token);
            return Accept("restarted done");
        });
        var fixture = Fixture(provider, 5, AgentTaskForkHistoryMode.Empty, cancellationToken);
        await using var registry = fixture.Runtime.Registry;
        await using var service = fixture.Service;
        Set(fixture, Declare("a", string.Empty));
        _ = await WaitFor(service, tasks => tasks[0].State == AgentTaskExecutionStatus.Succeeded, cancellationToken);
        var retained = fixture.Runtime.Sessions.ResolveScope("a");
        _ = await Assert.That(service.Snapshot()[0].Hidden).IsTrue();
        Set(fixture, Declare("a", ",\"state\":\"succeeded\",\"hidden\":false"));
        _ = await Assert.That(service.Snapshot()[0].Hidden).IsFalse();

        Set(fixture, Declare("a", ",\"state\":\"pending\",\"hidden\":true"));
        await arrived.Task.WaitAsync(cancellationToken);
        _ = await Assert.That(service.Snapshot()[0].State).IsEqualTo(AgentTaskExecutionStatus.Running);
        _ = await Assert.That(service.Snapshot()[0].Hidden).IsTrue();
        Set(fixture, Declare("a", ",\"state\":\"running\""));
        _ = await Assert.That(service.Snapshot()[0].Hidden).IsFalse();
        release.SetResult();
        var completed = await WaitFor(service, tasks => tasks[0].State == AgentTaskExecutionStatus.Succeeded, cancellationToken);
        _ = await Assert.That(completed[0].Hidden).IsTrue();
        _ = await Assert.That(completed[0].Result).IsEqualTo("restarted done");
        _ = await Assert.That(ReferenceEquals(retained, fixture.Runtime.Sessions.ResolveScope("a"))).IsTrue();
        _ = await Assert.That(provider.Requests.Count(AgentTaskReplyProvider.IsTaskAgent)).IsEqualTo(2);
    }

    [Test]
    public async Task Recursive_declarations_do_not_control_child_visibility_or_independent_success_hiding(CancellationToken cancellationToken)
    {
        var arrivals = new ConcurrentDictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
        var releases = new ConcurrentDictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
        foreach (var name in new[] { "root", "branch", "leaf", "peer" })
        {
            arrivals[name] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            releases[name] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        RuntimeContext? runtime = null;
        var provider = new AgentTaskReplyProvider(async (request, token) =>
        {
            if (!AgentTaskReplyProvider.IsTaskAgent(request))
            {
                return "noted";
            }

            var name = TaskName(request);
            if (!AgentTaskReplyProvider.Prompt(request).Contains("Task: ", StringComparison.Ordinal))
            {
                return Accept(name + " done");
            }

            if (name is "root" or "branch" && AgentTaskReplyProvider.Prompt(request).Contains("AgentTask role: composite owner", StringComparison.Ordinal))
            {
                var context = runtime ?? throw new InvalidOperationException("The runtime is not ready.");
                var childTasks = context.Sessions.ResolveScope(name).GetService<IAgentTaskService>();
                childTasks.SetTasks(PromptTasks(request), context.Selection, new HistoryForkBoundary.AfterCompletedHistory());
            }

            _ = arrivals[name].TrySetResult();
            await releases[name].Task.WaitAsync(token);
            return Accept(name + " done");
        });
        var fixture = Fixture(provider, 5, AgentTaskForkHistoryMode.Empty, cancellationToken);
        runtime = fixture.Runtime;
        await using var registry = fixture.Runtime.Registry;
        await using var service = fixture.Service;
        var leaf = Declare("leaf", string.Empty);
        var branch = Declare("branch", string.Empty).Replace("\"Work on branch\"", "[" + leaf + "]", StringComparison.Ordinal);
        var root = Declare("root", string.Empty).Replace("\"Work on root\"", "[" + branch + "," + Declare("peer", string.Empty) + "]", StringComparison.Ordinal);
        Set(fixture, root);
        await Task.WhenAll(arrivals.Values.Select(arrival => arrival.Task)).WaitAsync(cancellationToken);
        var rootScope = fixture.Runtime.Sessions.ResolveScope("root");
        var branchScope = fixture.Runtime.Sessions.ResolveScope("branch");
        var children = rootScope.GetService<IAgentTaskService>();
        var grandchildren = branchScope.GetService<IAgentTaskService>();
        var hiddenLeaf = Declare("leaf", ",\"hidden\":true");
        var hiddenBranch = Declare("branch", ",\"hidden\":true").Replace("\"Work on branch\"", "[" + hiddenLeaf + "]", StringComparison.Ordinal);
        var update = Declare("root", ",\"state\":\"running\",\"hidden\":true").Replace("\"Work on root\"", "[" + hiddenBranch + "," + Declare("peer", string.Empty) + "]", StringComparison.Ordinal);
        Set(fixture, update);
        _ = await Assert.That(children.Snapshot()[0].Hidden).IsFalse();
        _ = await Assert.That(children.Snapshot()[1].Hidden).IsFalse();
        _ = await Assert.That(grandchildren.Snapshot()[0].Hidden).IsFalse();
        _ = await Assert.That(children.Snapshot().All(task => task.State == AgentTaskExecutionStatus.Running)).IsTrue();
        _ = await Assert.That(grandchildren.Snapshot()[0].State).IsEqualTo(AgentTaskExecutionStatus.Running);

        Set(fixture, root.Replace("\"acceptance_criteria\":\"root works\"", "\"acceptance_criteria\":\"root works\",\"state\":\"running\"", StringComparison.Ordinal));
        _ = await Assert.That(children.Snapshot().Any(task => task.Hidden)).IsFalse();
        _ = await Assert.That(grandchildren.Snapshot()[0].Hidden).IsFalse();
        _ = await Assert.That(provider.Requests.Count(request => AgentTaskReplyProvider.IsTaskAgent(request) && AgentTaskReplyProvider.Prompt(request).Contains("Task: ", StringComparison.Ordinal))).IsEqualTo(4);
        releases["leaf"].SetResult();
        _ = await WaitFor(grandchildren, tasks => tasks[0].State == AgentTaskExecutionStatus.Succeeded, cancellationToken);
        _ = await Assert.That(grandchildren.Snapshot()[0].Hidden).IsTrue();
        _ = await Assert.That(children.Snapshot().Any(task => task.Hidden)).IsFalse();
        releases["branch"].SetResult();
        releases["peer"].SetResult();
        _ = await WaitFor(children, tasks => tasks.All(task => task.State == AgentTaskExecutionStatus.Succeeded), cancellationToken);
        _ = await Assert.That(children.Snapshot().All(task => task.Hidden)).IsTrue();
        _ = await Assert.That(service.Snapshot()[0].Hidden).IsFalse();
        releases["root"].SetResult();
        _ = await WaitFor(service, tasks => tasks[0].State == AgentTaskExecutionStatus.Succeeded, cancellationToken);
        _ = await Assert.That(service.Snapshot()[0].Hidden).IsTrue();
        _ = await Assert.That(fixture.Runtime.Sessions.Identities).Count().IsEqualTo(4);
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
        var fixture = Fixture(provider, 5, AgentTaskForkHistoryMode.Empty, cancellationToken);
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
            AgentTaskReplyProvider.Prompt(request).Contains("Task: ", StringComparison.Ordinal)
                ? Accept("task result")
                : "manual history"));
        var fixture = Fixture(provider, 5, AgentTaskForkHistoryMode.Empty, cancellationToken);
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
            AgentCompletionDeliveryPolicy.Automatic,
            new AgentHistorySource.Parent()));
        using var turns = _broker.Subscribe();
        _ = await manualScope.Session.SendAndWaitForResult([ConversationPart.TextPart("manual prompt")], Identifier.MessageId(), null, null, cancellationToken);
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
        Set(fixture, Declare("new-agent", ",\"model\":\"missing-provider/missing-model\""));
        var failed = await WaitFor(service, tasks => tasks.Single(task => task.Name == "new-agent").State == AgentTaskExecutionStatus.Failed, cancellationToken);
        _ = await Assert.That(failed.Single(task => task.Name == "new-agent").Failure)
            .IsEqualTo("provider: unknown provider \"missing-provider\"");
        _ = await Assert.That(runtime.Sessions.Identities).Count().IsEqualTo(1);
        _ = await Assert.That(LastProgress(runtime.Parent.SessionId).RootNodes.Single(node => node.Name == "new-agent").AgentSessionId).IsEmpty();
        Set(fixture, Declare("new-agent", string.Empty));
        _ = await WaitFor(service, tasks => tasks.All(task => task.State == AgentTaskExecutionStatus.Succeeded), cancellationToken);
        _ = await Assert.That(runtime.Sessions.Identities).Count().IsEqualTo(2);
    }

    [Test]
    [Arguments(AgentTaskForkHistoryMode.Dependency)]
    [Arguments(AgentTaskForkHistoryMode.Parent)]
    [Arguments(AgentTaskForkHistoryMode.Empty)]
    public async Task Forks_by_mode_using_the_first_declared_dependency_not_completion_order(
        AgentTaskForkHistoryMode mode,
        CancellationToken cancellationToken)
    {
        var firstArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new AgentTaskReplyProvider(async (request, token) =>
        {
            if (!AgentTaskReplyProvider.IsTaskAgent(request))
            {
                return "noted";
            }

            var name = TaskName(request);
            if (name == "first")
            {
                firstArrived.SetResult();
                await releaseFirst.Task.WaitAsync(token);
            }

            return Accept(name + "-transcript-marker");
        });
        var fixture = Fixture(provider, 5, mode, cancellationToken);
        await using var registry = fixture.Runtime.Registry;
        await using var service = fixture.Service;
        _repository.AppendConversation(
            new Event { Id = "owner-before", AgentSessionId = fixture.Runtime.Parent.SessionId },
            ConversationOrigin.UserInput,
            LLMRole.User,
            [ConversationPart.TextPart("owner-history-marker")],
            [],
            string.Empty);

        Set(
            fixture,
            Declare("next", ",\"dependencies\":[\"second\",\"first\"]"),
            Declare("second", string.Empty),
            Declare("first", string.Empty));
        await firstArrived.Task.WaitAsync(cancellationToken);
        _ = await WaitFor(service, tasks => tasks.Single(task => task.Name == "second").State == AgentTaskExecutionStatus.Succeeded, cancellationToken);
        Set(fixture, Declare("next", ",\"dependencies\":[\"first\",\"second\"]"));
        _ = await Assert.That(fixture.Runtime.Sessions.Identities.Select(identity => identity.Name)).DoesNotContain("next");
        _ = await Assert.That(LastProgress(fixture.Runtime.Parent.SessionId).RootNodes.Single(node => node.Name == "next").AgentSessionId).IsEmpty();
        releaseFirst.SetResult();
        _ = await WaitFor(service, tasks => tasks.All(task => task.State == AgentTaskExecutionStatus.Succeeded), cancellationToken);

        var next = provider.Requests.Single(request => AgentTaskReplyProvider.IsTaskAgent(request) && TaskName(request) == "next");
        var assistantHistory = next.Messages.Where(message => message.Role == LLMRole.Assistant).Select(message => message.Content).ToArray();
        _ = await Assert.That(assistantHistory.Contains(Accept("first-transcript-marker"), StringComparer.Ordinal))
            .IsEqualTo(mode == AgentTaskForkHistoryMode.Dependency);
        _ = await Assert.That(assistantHistory).DoesNotContain(Accept("second-transcript-marker"));
        _ = await Assert.That(next.Messages.Any(message => message.Content == "owner-history-marker"))
            .IsEqualTo(mode != AgentTaskForkHistoryMode.Empty);
        _ = await Assert.That(AgentTaskReplyProvider.Prompt(next)).Contains("[first] Do first\nResult: first-transcript-marker")
            .And.Contains("[second] Do second\nResult: second-transcript-marker");
        var retained = fixture.Runtime.Sessions.ResolveScope("next");
        _ = await Assert.That(retained.Session.ParentSessionId).IsEqualTo(fixture.Runtime.Parent.SessionId);
        Set(fixture, Declare("next", ",\"dependencies\":[\"second\",\"first\"]"));
        _ = await WaitFor(service, tasks => tasks.All(task => task.State == AgentTaskExecutionStatus.Succeeded), cancellationToken);
        _ = await Assert.That(fixture.Runtime.Sessions.ResolveScope("next")).IsSameReferenceAs(retained);
        var restarted = provider.Requests.Last(request => AgentTaskReplyProvider.IsTaskAgent(request) && TaskName(request) == "next");
        _ = await Assert.That(restarted.Messages.Where(message => message.Role == LLMRole.Assistant).Select(message => message.Content))
            .Contains(Accept("next-transcript-marker"))
            .And.DoesNotContain(Accept("second-transcript-marker"));
        _ = await Assert.That(fixture.Runtime.Sessions.Identities).Count().IsEqualTo(3);
    }

    [Test]
    public async Task Historyless_manual_success_falls_back_before_the_owners_invoking_batch_without_creating_terminal_agents(
        CancellationToken cancellationToken)
    {
        var provider = new AgentTaskReplyProvider((request, _) => Task.FromResult(
            AgentTaskReplyProvider.IsTaskAgent(request) ? Accept("next done") : "noted"));
        var fixture = Fixture(provider, 5, AgentTaskForkHistoryMode.Dependency, cancellationToken);
        await using var registry = fixture.Runtime.Registry;
        await using var service = fixture.Service;
        var owner = fixture.Runtime.Parent.SessionId;
        _repository.AppendConversation(
            new Event { Id = "owner-safe", AgentSessionId = owner },
            ConversationOrigin.UserInput,
            LLMRole.User,
            [ConversationPart.TextPart("owner-safe-marker")],
            [],
            string.Empty);
        _repository.AppendConversation(
            new Event { Id = "owner-invocation", AgentSessionId = owner },
            ConversationOrigin.Model,
            LLMRole.Assistant,
            [ConversationPart.TextPart("invoking-batch-marker")],
            [new LLMToolCall("set-tasks", "set_agent_tasks", "{}")],
            string.Empty);
        var boundary = new HistoryForkBoundary.BeforeToolBatch(_repository.Conversation(owner)[^1].Sequence, "set-tasks");
        service.SetTasks(
            Tasks(
                Declare("manual", ",\"state\":\"succeeded\",\"result\":\"manually accepted\""),
                Declare("canceled", ",\"state\":\"canceled\""),
                Declare("held", ",\"dependencies\":[\"canceled\"]"),
                Declare("next", ",\"dependencies\":[\"manual\"]")),
            fixture.Runtime.Selection,
            boundary);
        _ = await WaitFor(service, tasks => tasks.Single(task => task.Name == "next").State == AgentTaskExecutionStatus.Succeeded, cancellationToken);
        _ = await Assert.That(fixture.Runtime.Sessions.Identities.Select(identity => identity.Name)).IsEquivalentTo(["next"]);
        _ = await Assert.That(LastProgress(owner).RootNodes.Where(node => node.Name != "next").Select(node => node.AgentSessionId))
            .IsEquivalentTo([string.Empty, string.Empty, string.Empty]);
        var next = provider.Requests.Single(AgentTaskReplyProvider.IsTaskAgent);
        _ = await Assert.That(next.Messages.Select(message => message.Content)).Contains("owner-safe-marker")
            .And.DoesNotContain("invoking-batch-marker");
        _ = await Assert.That(AgentTaskReplyProvider.Prompt(next)).Contains("manually accepted");
    }

    [Test]
    public async Task Compacted_owner_boundary_fails_the_deferred_task_without_creating_an_agent(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskReplyProvider((_, _) => Task.FromResult("noted"));
        var fixture = Fixture(provider, 5, AgentTaskForkHistoryMode.Dependency, cancellationToken);
        await using var registry = fixture.Runtime.Registry;
        await using var service = fixture.Service;
        var owner = fixture.Runtime.Parent.SessionId;
        _repository.AppendConversation(
            new Event { Id = "owner-launch", AgentSessionId = owner },
            ConversationOrigin.Model,
            LLMRole.Assistant,
            [],
            [new LLMToolCall("launch", "set_agent_tasks", "{}")],
            string.Empty);
        var launch = _repository.Conversation(owner)[^1].Sequence;
        _ = _repository.AppendToolSettlement(
            new Event { Id = "owner-launch-result", AgentSessionId = owner },
            launch,
            new ToolExecutionTerminal("launch", "set_agent_tasks", ToolExecutionStatus.Finished, [], "declared"));
        service.SetTasks(
            Tasks(
                Declare("manual", ",\"state\":\"failed\",\"failure\":\"awaiting acceptance\""),
                Declare("next", ",\"dependencies\":[\"manual\"]")),
            fixture.Runtime.Selection,
            new HistoryForkBoundary.BeforeToolBatch(launch, "launch"));
        _ = _repository.AppendCompactionStatus(
            new Event { Id = "owner-compacted", AgentSessionId = owner },
            new CompactionSnapshot("later owner summary", _repository.Conversation(owner)[^1].Sequence),
            "later status");
        Set(fixture, Declare("manual", ",\"state\":\"succeeded\",\"result\":\"accepted\""));
        var failed = service.Snapshot().Single(task => task.Name == "next");
        _ = await Assert.That(failed.State).IsEqualTo(AgentTaskExecutionStatus.Failed);
        _ = await Assert.That(failed.Failure).Contains("captured fork tool batch is unavailable in effective history; it may have been compacted");
        _ = await Assert.That(fixture.Runtime.Sessions.Identities).IsEmpty();
        _ = await Assert.That(LastProgress(owner).RootNodes.Single(node => node.Name == "next").AgentSessionId).IsEmpty();
    }

    [Test]
    public async Task Explicit_success_of_an_active_dependency_forks_only_its_safe_prefix(CancellationToken cancellationToken)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new AgentTaskReplyProvider(async (request, token) =>
        {
            if (!AgentTaskReplyProvider.IsTaskAgent(request))
            {
                return "noted";
            }

            if (TaskName(request) == "source")
            {
                arrived.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            return Accept("next done");
        });
        var fixture = Fixture(provider, 5, AgentTaskForkHistoryMode.Dependency, cancellationToken);
        await using var registry = fixture.Runtime.Registry;
        await using var service = fixture.Service;
        Set(fixture, Declare("source", string.Empty), Declare("next", ",\"dependencies\":[\"source\"]"));
        await arrived.Task.WaitAsync(cancellationToken);
        var source = fixture.Runtime.Sessions.ResolveScope("source").Session.SessionId;
        _repository.AppendConversation(
            new Event { Id = "source-safe", AgentSessionId = source },
            ConversationOrigin.UserInput,
            LLMRole.User,
            [ConversationPart.TextPart("source-safe-marker")],
            [],
            string.Empty);
        _repository.AppendConversation(
            new Event { Id = "source-incomplete", AgentSessionId = source },
            ConversationOrigin.Model,
            LLMRole.Assistant,
            [ConversationPart.TextPart("incomplete-marker")],
            [new LLMToolCall("unfinished", "exec_command", "{}")],
            string.Empty);
        _repository.AppendConversation(
            new Event { Id = "source-after", AgentSessionId = source },
            ConversationOrigin.UserInput,
            LLMRole.User,
            [ConversationPart.TextPart("after-incomplete-marker")],
            [],
            string.Empty);
        Set(fixture, Declare("source", ",\"state\":\"succeeded\",\"result\":\"accepted while active\""));
        _ = await WaitFor(service, tasks => tasks.All(task => task.State == AgentTaskExecutionStatus.Succeeded), cancellationToken);
        var next = provider.Requests.Single(request => AgentTaskReplyProvider.IsTaskAgent(request) && TaskName(request) == "next");
        _ = await Assert.That(next.Messages.Select(message => message.Content)).Contains("source-safe-marker")
            .And.DoesNotContain("incomplete-marker")
            .And.DoesNotContain("after-incomplete-marker");
        _ = await Assert.That(next.Messages.Any(message => message.ToolCalls.Count > 0)).IsFalse();
        _ = await Assert.That(AgentTaskReplyProvider.Prompt(next)).Contains("accepted while active");
    }

    private static IReadOnlyList<AgentTask> PromptTasks(LLMRequest request)
    {
        const string marker = "The following inner task list is provided:\n";
        var prompt = AgentTaskReplyProvider.Prompt(request);
        var start = prompt.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = prompt.IndexOf("\nPrepare common resource or context", start, StringComparison.Ordinal);
        return AgentTaskParser.ParseTaskSet(prompt[start..end]);
    }

    private static string Accept(string result) => $$"""{"result":"{{result}}","verdict":"accept"}""";

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
        var prompt = request.Messages.Last(message => message.Role == LLMRole.User
            && message.Content.Contains("Task: ", StringComparison.Ordinal)).Content;
        var start = prompt.IndexOf("Task: ", StringComparison.Ordinal) + "Task: ".Length;
        return prompt[start..prompt.IndexOf('\n', start)];
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

    private (RuntimeContext Runtime, AgentTaskService Service) Fixture(ILLMProvider provider, int maximumAttempts, AgentTaskForkHistoryMode forkMode, CancellationToken cancellationToken)
    {
        var runtime = Runtime(provider, cancellationToken);
        return (runtime, new AgentTaskService(
            runtime.ParentScope,
            runtime.Parent.SessionId,
            runtime.Router,
            new AgentTaskConfig(maximumAttempts, 1, forkMode, TestModels.PromptTemplates),
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
            selected.Profile,
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
            IAgentProfile profile,
            SecurityProfile securityProfile,
            IAgentRegistry registry,
            CancellationToken lifetime) =>
            fail ? throw new InvalidOperationException("secret-exception") : scope;
    }
}
