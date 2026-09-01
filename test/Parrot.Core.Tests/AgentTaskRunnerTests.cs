using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Security;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class AgentTaskRunnerTests : IDisposable
{
    private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");
    private readonly EventBroker _broker = new();
    private readonly EventRepository _repository;

    public AgentTaskRunnerTests() => _repository = new EventRepository(_database);

    public void Dispose()
    {
        _broker.Dispose();
        _database.Dispose();
    }

    [Test]
    public async Task Runs_leaf_with_sparse_research_context_and_acceptance(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"contract evidence\"}",
            "implemented output",
            "{\"verdict\":\"accept\",\"evidence\":\"tests passed\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"leaf","description":"Implement leaf","payload":"Do leaf work","acceptance_criteria":"Leaf is proven"}]}
            """);

        var result = await Runner(registry, runtime, "runner-call")
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        _ = await Assert.That(result.Tasks.Single().AttemptCount).IsEqualTo(1);
        _ = await Assert.That(result.Tasks.Single().Context).IsEqualTo("contract evidence");
        _ = await Assert.That(runtime.Sessions.Identities).Count().IsEqualTo(3);
        _ = await Assert.That(runtime.Sessions.Identities.All(identity => identity.ParentSessionId == runtime.Parent.SessionId)).IsTrue();
        _ = await Assert.That(provider.Requests.All(request => request.Messages.Count(message => message.Role != LLMRole.System) == 1)).IsTrue();
        _ = await Assert.That(provider.Requests[1].Messages.Select(message => message.Content)).Contains(message => message.Contains("contract evidence", StringComparison.Ordinal));

        var snapshots = ProgressEvents("runner-call");
        _ = await Assert.That(string.Join(',', snapshots.Select(snapshot => snapshot.Revision)))
            .IsEqualTo("1,2,3");
        _ = await Assert.That(snapshots[0].RootNodes.Single().Status)
            .IsEqualTo(AgentTaskProgressStatus.Pending);
        _ = await Assert.That(snapshots[1].RootNodes.Single().Status)
            .IsEqualTo(AgentTaskProgressStatus.Running);
        _ = await Assert.That(snapshots[2].RootNodes.Single().Status)
            .IsEqualTo(AgentTaskProgressStatus.Succeeded);
    }

    [Test]
    public async Task Acceptance_prompt_describes_the_strict_verdict_contract(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"contract evidence\"}",
            "implemented output",
            "{\"verdict\":\"accept\",\"evidence\":\"tests passed\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"leaf","description":"Implement leaf","payload":"Do leaf work","acceptance_criteria":"Leaf is proven"}]}
            """);

        _ = await Runner(registry, runtime, "acceptance-contract")
            .Run(artifact, cancellationToken);

        var prompt = provider.Requests[2].Messages.Single(message => message.Role == LLMRole.User).Content;
        _ = await Assert.That(prompt).Contains("Return only one strict JSON object with no prose or code fence:");
        _ = await Assert.That(prompt).Contains("{\"verdict\":\"accept\",\"evidence\":\"nonblank\"}");
        _ = await Assert.That(prompt).Contains("{\"verdict\":\"reject_and_halt\",\"feedback\":\"nonblank\"}");
        _ = await Assert.That(prompt).Contains("{\"verdict\":\"reject_and_retry\",\"feedback\":\"nonblank\",\"payload\":\"replacement instruction or task array\"");
    }

    [Test]
    public async Task Halt_rejection_does_not_start_a_second_attempt(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"contract evidence\"}",
            "implemented output",
            "{\"verdict\":\"reject_and_halt\",\"feedback\":\"not done\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"leaf","description":"Implement leaf","payload":"Do leaf work","acceptance_criteria":"Leaf is proven"}]}
            """);

        var result = await RunnerWithAttempts(registry, runtime, "halt-contract", 2)
            .Run(artifact, cancellationToken);

        var task = result.Tasks.Single();
        _ = await Assert.That(task.Status).IsEqualTo(AgentTaskExecutionStatus.Failed);
        _ = await Assert.That(task.AttemptCount).IsEqualTo(1);
        _ = await Assert.That(task.Verdict?.Kind).IsEqualTo(AcceptanceVerdictKind.RejectAndHalt);
        _ = await Assert.That(task.Failure).IsEqualTo("not done");
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(3);
    }

    [Test]
    public async Task Retries_payload_five_times_without_repeating_research(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"stable research\"}",
            "first output",
            "{\"verdict\":\"reject_and_retry\",\"feedback\":\"fix first\",\"payload\":\"second payload\"}",
            "second output",
            "{\"verdict\":\"reject_and_retry\",\"feedback\":\"fix second\",\"payload\":\"third payload\"}",
            "third output",
            "{\"verdict\":\"reject_and_retry\",\"feedback\":\"still bad\",\"payload\":\"fourth payload\"}",
            "fourth output",
            "{\"verdict\":\"reject_and_retry\",\"feedback\":\"still bad\",\"payload\":\"fifth payload\"}",
            "fifth output",
            "{\"verdict\":\"reject_and_retry\",\"feedback\":\"still bad\",\"payload\":\"sixth payload\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"retry","description":"Retry task","payload":"first payload","acceptance_criteria":"Must pass"}]}
            """);

        var result = await Runner(registry, runtime, "runner-call")
            .Run(artifact, cancellationToken);

        var task = result.Tasks.Single();
        _ = await Assert.That(task.Status).IsEqualTo(AgentTaskExecutionStatus.Failed);
        _ = await Assert.That(task.AttemptCount).IsEqualTo(5);
        _ = await Assert.That(task.Context).IsEqualTo("stable research");
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(11);
        _ = await Assert.That(provider.Requests.Count(request => request.Messages.Select(message => message.Content).Any(content => content.Contains("research pre-hook", StringComparison.Ordinal)))).IsEqualTo(1);
    }

    [Test]
    public async Task Configured_attempt_budget_limits_each_task_without_repeating_research(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"stable research\"}",
            "first output",
            "{\"verdict\":\"reject_and_retry\",\"feedback\":\"fix first\",\"payload\":\"second payload\"}",
            "second output",
            "{\"verdict\":\"reject_and_retry\",\"feedback\":\"fix second\",\"payload\":\"third payload\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"retry","description":"Retry task","payload":"first payload","acceptance_criteria":"Must pass"}]}
            """);

        var result = await RunnerWithAttempts(registry, runtime, "runner-call", 2)
            .Run(artifact, cancellationToken);

        var task = result.Tasks.Single();
        _ = await Assert.That(task.Status).IsEqualTo(AgentTaskExecutionStatus.Failed);
        _ = await Assert.That(task.AttemptCount).IsEqualTo(2);
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(5);
        _ = await Assert.That(provider.Requests.Count(request => request.Messages.Select(message => message.Content)
            .Any(content => content.Contains("research pre-hook", StringComparison.Ordinal)))).IsEqualTo(1);
    }

    [Test]
    public async Task Successful_retry_retains_ordered_feedback(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"stable research\"}",
            "first output",
            "{\"verdict\":\"reject_and_retry\",\"feedback\":\"fix first\",\"payload\":\"second payload\"}",
            "second output",
            "{\"verdict\":\"reject_and_retry\",\"feedback\":\"fix second\",\"payload\":\"third payload\"}",
            "third output",
            "{\"verdict\":\"accept\",\"evidence\":\"done\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"retry","description":"Retry task","payload":"first payload","acceptance_criteria":"Must pass"}]}
            """);

        var result = await Runner(registry, runtime, "runner-call")
            .Run(artifact, cancellationToken);

        var task = result.Tasks.Single();
        _ = await Assert.That(task.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        _ = await Assert.That(task.AttemptCount).IsEqualTo(3);
        _ = await Assert.That(string.Join(",", task.RetryFeedback ?? [])).IsEqualTo("fix first,fix second");
        using var serialized = System.Text.Json.JsonDocument.Parse(result.Serialize());
        _ = await Assert.That(string.Join(",", serialized.RootElement.GetProperty("tasks")[0]
            .GetProperty("retry_feedback").EnumerateArray().Select(item => item.GetString())))
            .IsEqualTo("fix first,fix second");
    }

    [Test]
    public async Task Retry_context_replaces_later_prompt_and_final_result(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"initial context\"}",
            "first output",
            "{\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\",\"payload\":\"second payload\",\"context\":\"replacement context\"}",
            "second output",
            "{\"verdict\":\"accept\",\"evidence\":\"done\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"retry","description":"Retry task","payload":"first payload","acceptance_criteria":"Must pass"}]}
            """);

        var result = await Runner(registry, runtime, "replacement-context")
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Tasks.Single().Context).IsEqualTo("replacement context");
        var secondExecution = provider.Requests[3].Messages.Single(message => message.Role == LLMRole.User).Content;
        _ = await Assert.That(secondExecution).Contains("replacement context");
        _ = await Assert.That(secondExecution).DoesNotContain("initial context");
        var secondAcceptance = provider.Requests[4].Messages.Single(message => message.Role == LLMRole.User).Content;
        _ = await Assert.That(secondAcceptance).Contains("replacement context");
        _ = await Assert.That(secondAcceptance).DoesNotContain("initial context");
    }

    [Test]
    public async Task Retry_without_context_retains_prior_context(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"initial context\"}",
            "first output",
            "{\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\",\"payload\":\"second payload\"}",
            "second output",
            "{\"verdict\":\"accept\",\"evidence\":\"done\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"retry","description":"Retry task","payload":"first payload","acceptance_criteria":"Must pass"}]}
            """);

        var result = await Runner(registry, runtime, "retained-context")
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Tasks.Single().Context).IsEqualTo("initial context");
        var secondExecution = provider.Requests[3].Messages.Single(message => message.Role == LLMRole.User).Content;
        _ = await Assert.That(secondExecution).Contains("initial context");
    }

    [Test]
    public async Task Final_retry_retains_replacement_context_without_further_execution(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"initial context\"}",
            "output",
            "{\"verdict\":\"reject_and_retry\",\"feedback\":\"fix\",\"payload\":\"replacement\",\"context\":\"final context\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"retry","description":"Retry task","payload":"first payload","acceptance_criteria":"Must pass"}]}
            """);

        var result = await RunnerWithAttempts(registry, runtime, "final-context", 1)
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Tasks.Single().Status).IsEqualTo(AgentTaskExecutionStatus.Failed);
        _ = await Assert.That(result.Tasks.Single().Context).IsEqualTo("final context");
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(3);
    }

    [Test]
    public async Task Composite_acceptance_receives_failed_child_evidence(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"parent context\"}",
            "{\"context\":\"child context\"}",
            "child output",
            "{\"verdict\":\"reject_and_halt\",\"feedback\":\"child proof failed\"}",
            "{\"verdict\":\"accept\",\"evidence\":\"parent explicitly accepts failure\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"parent","description":"Parent","payload":[{"name":"child","description":"Child","payload":"child work","acceptance_criteria":"Child proof"}],"acceptance_criteria":"Parent decides"}]}
            """);

        var result = await Runner(registry, runtime, "runner-call")
            .Run(artifact, cancellationToken);

        var task = result.Tasks.Single();
        _ = await Assert.That(task.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        _ = await Assert.That(task.Tasks?.Single().Status).IsEqualTo(AgentTaskExecutionStatus.Failed);
        var finalProgress = ProgressEvents("runner-call")[^1].RootNodes.Single();
        _ = await Assert.That(finalProgress.Status).IsEqualTo(AgentTaskProgressStatus.Succeeded);
        _ = await Assert.That(finalProgress.Children.Single().Status).IsEqualTo(AgentTaskProgressStatus.Failed);
        var acceptance = provider.Requests[^1].Messages.Single(message => message.Role == LLMRole.User).Content;
        _ = await Assert.That(acceptance).Contains("Nested task results (structured JSON):");
        _ = await Assert.That(acceptance).Contains("child proof failed");
        _ = await Assert.That(acceptance).Contains("\"status\":\"failed\"");
    }

    [Test]
    public async Task Research_patch_replaces_the_effective_progress_subtree(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"parent context\",\"task_patch\":{\"payload\":[{\"name\":\"new-child\",\"description\":\"New child\",\"payload\":\"new work\",\"acceptance_criteria\":\"New proof\"}]}}",
            "{\"context\":\"new child context\"}",
            "new child output",
            "{\"verdict\":\"accept\",\"evidence\":\"new child done\"}",
            "{\"verdict\":\"accept\",\"evidence\":\"parent done\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"parent","description":"Parent","payload":[{"name":"old-child","description":"Old child","payload":"old work","acceptance_criteria":"Old proof"}],"acceptance_criteria":"Parent proof"}]}
            """);

        var result = await Runner(registry, runtime, "research-replacement")
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        var snapshots = ProgressEvents("research-replacement");
        var replacement = snapshots.Single(snapshot =>
            snapshot.RootNodes[0].Children.Count == 1
            && snapshot.RootNodes[0].Children[0].Name == "new-child"
            && snapshot.RootNodes[0].Children[0].Status == AgentTaskProgressStatus.Pending);
        _ = await Assert.That(replacement.RootNodes[0].Children.Select(node => node.Name))
            .DoesNotContain("old-child");
        _ = await Assert.That(snapshots.SkipWhile(snapshot => snapshot.Revision < replacement.Revision)
            .SelectMany(snapshot => snapshot.RootNodes[0].Children)
            .Select(node => node.Name))
            .DoesNotContain("old-child");
        _ = await Assert.That(snapshots[^1].RootNodes[0].Children.Single().Status)
            .IsEqualTo(AgentTaskProgressStatus.Succeeded);
    }

    [Test]
    public async Task Retry_payload_replaces_stale_progress_descendants(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"parent context\"}",
            "first output",
            "{\"verdict\":\"reject_and_retry\",\"feedback\":\"split it\",\"payload\":[{\"name\":\"retry-child\",\"description\":\"Retry child\",\"payload\":\"child work\",\"acceptance_criteria\":\"Child proof\"}],\"context\":\"replacement parent context\"}",
            "{\"context\":\"retry child context\"}",
            "retry child output",
            "{\"verdict\":\"accept\",\"evidence\":\"child done\"}",
            "{\"verdict\":\"accept\",\"evidence\":\"parent done\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"parent","description":"Parent","payload":"first work","acceptance_criteria":"Parent proof"}]}
            """);

        var result = await Runner(registry, runtime, "retry-replacement")
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        _ = await Assert.That(result.Tasks.Single().Context).IsEqualTo("replacement parent context");
        _ = await Assert.That(result.Tasks.Single().Tasks?.Single().Context).IsEqualTo("retry child context");
        var childExecution = provider.Requests[4].Messages.Single(message => message.Role == LLMRole.User).Content;
        _ = await Assert.That(childExecution).Contains("replacement parent context");
        _ = await Assert.That(childExecution).Contains("retry child context");
        _ = await Assert.That(childExecution).DoesNotContain("\n[task/parent] parent context");
        var snapshots = ProgressEvents("retry-replacement");
        _ = await Assert.That(snapshots.TakeWhile(snapshot => snapshot.RootNodes[0].Children.Count == 0))
            .IsNotEmpty();
        var replacement = snapshots.First(snapshot => snapshot.RootNodes[0].Children.Count > 0);
        _ = await Assert.That(replacement.RootNodes[0].Children.Single().Name).IsEqualTo("retry-child");
        _ = await Assert.That(replacement.RootNodes[0].Children.Single().Status)
            .IsEqualTo(AgentTaskProgressStatus.Pending);
        _ = await Assert.That(snapshots[^1].RootNodes[0].Children.Single().Status)
            .IsEqualTo(AgentTaskProgressStatus.Succeeded);
    }

    [Test]
    public async Task Failed_dependency_blocks_the_complete_pending_subtree(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"prerequisite context\"}",
            "prerequisite output",
            "{\"verdict\":\"reject_and_halt\",\"feedback\":\"not done\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[
              {"name":"prerequisite","description":"Prerequisite","payload":"work","acceptance_criteria":"Done"},
              {"name":"dependent","dependencies":["prerequisite"],"description":"Dependent","payload":[{"name":"nested","description":"Nested","payload":"nested work","acceptance_criteria":"Nested done"}],"acceptance_criteria":"Dependent done"}
            ]}
            """);

        var result = await Runner(registry, runtime, "blocked-subtree")
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Failed);
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(3);
        var final = ProgressEvents("blocked-subtree")[^1];
        _ = await Assert.That(final.RootNodes[0].Status).IsEqualTo(AgentTaskProgressStatus.Failed);
        _ = await Assert.That(final.RootNodes[1].Status).IsEqualTo(AgentTaskProgressStatus.Blocked);
        _ = await Assert.That(final.RootNodes[1].Children.Single().Status)
            .IsEqualTo(AgentTaskProgressStatus.Blocked);
    }

    [Test]
    public async Task Schedules_ready_tasks_concurrently_and_releases_dependents_immediately(
        CancellationToken cancellationToken)
    {
        var provider = new AgentTaskSchedulingProvider();
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[
              {"name":"fast","description":"Fast","payload":"fast work","acceptance_criteria":"Done"},
              {"name":"slow","description":"Slow","payload":"slow work","acceptance_criteria":"Done"},
              {"name":"dependent","dependencies":["fast"],"description":"Dependent","payload":"dependent work","acceptance_criteria":"Done"}
            ]}
            """);

        var result = await Runner(registry, runtime, "runner-call")
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        _ = await Assert.That(provider.MaximumActive >= 2).IsTrue();
        _ = await Assert.That(provider.DependentStartedBeforeSlowFinished).IsTrue();
        _ = await Assert.That(string.Join(",", result.Tasks.Select(task => task.Name))).IsEqualTo("fast,slow,dependent");
        var snapshots = ProgressEvents("runner-call");
        _ = await Assert.That(snapshots.All(snapshot =>
            string.Join(',', snapshot.RootNodes.Select(node => node.Name)) == "fast,slow,dependent"))
            .IsTrue();
        var fastSucceeded = snapshots.ToList().FindIndex(snapshot =>
            snapshot.RootNodes[0].Status == AgentTaskProgressStatus.Succeeded);
        var dependentRunning = snapshots.ToList().FindIndex(snapshot =>
            snapshot.RootNodes[2].Status == AgentTaskProgressStatus.Running);
        _ = await Assert.That(dependentRunning > fastSucceeded).IsTrue();
    }

    [Test]
    public async Task Cancellation_interrupts_and_joins_active_internal_child(CancellationToken cancellationToken)
    {
        using var provider = new AgentTaskBlockingProvider();
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"cancel","description":"Cancel task","payload":"work","acceptance_criteria":"Done"}]}
            """);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = Runner(registry, runtime, "runner-call")
            .Run(artifact, canceled.Token);
        await provider.WaitUntilArrived(cancellationToken);

        await canceled.CancelAsync();
        _ = await Assert.That(running).Throws<OperationCanceledException>();

        _ = await Assert.That(registry.ActiveDirectChildren(runtime.Parent.SessionId)).IsEmpty();
        var snapshots = ProgressEvents("runner-call");
        _ = await Assert.That(snapshots[^1].RootNodes.Single().Status)
            .IsEqualTo(AgentTaskProgressStatus.Canceled);
        _ = await Assert.That(snapshots[^1].Revision).IsEqualTo((ulong)snapshots.Length);
        _ = await Assert.That(_repository.Replay()[^1].AgentTaskProgressSnapshot.Revision)
            .IsEqualTo(snapshots[^1].Revision);
    }

    private AgentTaskProgressSnapshot[] ProgressEvents(string originToolCallId) =>
        [.. _repository.Replay()
            .Where(published => published.PayloadCase == Event.PayloadOneofCase.AgentTaskProgressSnapshot)
            .Select(published => published.AgentTaskProgressSnapshot)
            .Where(snapshot => snapshot.OriginToolCallId == originToolCallId)];

    private AgentTaskGraphRunner Runner(
        AgentRegistry registry,
        RuntimeContext runtime,
        string originToolCallId) => RunnerWithAttempts(registry, runtime, originToolCallId, 5);

    private AgentTaskGraphRunner RunnerWithAttempts(
        AgentRegistry registry,
        RuntimeContext runtime,
        string originToolCallId,
        int maximumAttempts) => new(
            registry,
            runtime.Router,
            runtime.Parent,
            runtime.Selection,
            new AgentTaskProgress(
                _broker,
                _repository,
                runtime.Parent.SessionId,
                originToolCallId),
            new AgentTaskConfig(maximumAttempts));

    private RuntimeContext Runtime(ILLMProvider provider, CancellationToken cancellationToken)
    {
        var model = new LLMModel("model", provider.Id);
        var providers = new ProviderRegistry(
            [provider],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal) { [provider.Id] = [model] });
        var router = new ModelRouter(providers, new ModelAliasCatalog(providers, []), $"{provider.Id}/model");
        var sessions = new AgentTaskTestSessionFactory(router);
        var registry = TestModels.Registry(
            sessions,
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            cancellationToken);
        var identity = AgentIdentity.Main("agent-task-parent", "parent");
        var dependencies = TestModels.Dependencies(identity, _broker, _repository, cancellationToken);
        var parent = new AgentSession(
            identity,
            new ModelSelector($"{provider.Id}/model"),
            router,
            _broker,
            _repository,
            [],
            TestModels.EmptyToolDefinitions,
            TestModels.MaterializePrompt(identity, ".", "."),
            new TodoCollection(identity.SessionId, _repository, _broker),
            new ToolOutputBlobStore(Path.GetTempPath()),
            new Parrot.Context.Compactor(90, 30, 60_000, 1024),
            dependencies.ActiveWorkReminder,
            dependencies.Profile,
            SecurityProfileTestFactory.Create(SecurityProfile.Compose(false, [], [], [])),
            dependencies.Status,
            registry,
            dependencies.Queues,
            new AgentSessionActivity(TimeProvider.System),
            cancellationToken);
        var selected = parent.Selection();
        var selection = new AgentTurnSelection(
            selected.RequestedModel,
            router.Resolve(selected.RequestedModel.Value),
            selected.Profile,
            selected.SecurityProfile);
        return new RuntimeContext(router, sessions, registry, parent, selection);
    }

    private sealed record RuntimeContext(
        ModelRouter Router,
        AgentTaskTestSessionFactory Sessions,
        AgentRegistry Registry,
        AgentSession Parent,
        AgentTurnSelection Selection);
}
