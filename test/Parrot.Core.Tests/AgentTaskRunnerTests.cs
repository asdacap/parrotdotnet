using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Events;
using Parrot.Llm;
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

        var result = await new AgentTaskGraphRunner(registry, runtime.Router, runtime.Parent, runtime.Selection)
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        _ = await Assert.That(result.Tasks.Single().AttemptCount).IsEqualTo(1);
        _ = await Assert.That(result.Tasks.Single().Context).IsEqualTo("contract evidence");
        _ = await Assert.That(runtime.Sessions.Identities).Count().IsEqualTo(3);
        _ = await Assert.That(runtime.Sessions.Identities.All(identity => identity.ParentSessionId == runtime.Parent.SessionId)).IsTrue();
        _ = await Assert.That(provider.Requests.All(request => request.Messages.Count(message => message.Role != LLMRole.System) == 1)).IsTrue();
        _ = await Assert.That(provider.Requests[1].Messages.Select(message => message.Content)).Contains(message => message.Contains("contract evidence", StringComparison.Ordinal));
    }

    [Test]
    public async Task Retries_payload_three_times_without_repeating_research(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"stable research\"}",
            "first output",
            "{\"verdict\":\"retry\",\"feedback\":\"fix first\",\"payload\":\"second payload\"}",
            "second output",
            "{\"verdict\":\"retry\",\"feedback\":\"fix second\",\"payload\":\"third payload\"}",
            "third output",
            "{\"verdict\":\"retry\",\"feedback\":\"still bad\",\"payload\":\"fourth payload\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"retry","description":"Retry task","payload":"first payload","acceptance_criteria":"Must pass"}]}
            """);

        var result = await new AgentTaskGraphRunner(registry, runtime.Router, runtime.Parent, runtime.Selection)
            .Run(artifact, cancellationToken);

        var task = result.Tasks.Single();
        _ = await Assert.That(task.Status).IsEqualTo(AgentTaskExecutionStatus.Failed);
        _ = await Assert.That(task.AttemptCount).IsEqualTo(3);
        _ = await Assert.That(task.Context).IsEqualTo("stable research");
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(7);
        _ = await Assert.That(provider.Requests.Count(request => request.Messages.Select(message => message.Content).Any(content => content.Contains("research pre-hook", StringComparison.Ordinal)))).IsEqualTo(1);
    }

    [Test]
    public async Task Successful_retry_retains_ordered_feedback(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"stable research\"}",
            "first output",
            "{\"verdict\":\"retry\",\"feedback\":\"fix first\",\"payload\":\"second payload\"}",
            "second output",
            "{\"verdict\":\"retry\",\"feedback\":\"fix second\",\"payload\":\"third payload\"}",
            "third output",
            "{\"verdict\":\"accept\",\"evidence\":\"done\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"retry","description":"Retry task","payload":"first payload","acceptance_criteria":"Must pass"}]}
            """);

        var result = await new AgentTaskGraphRunner(registry, runtime.Router, runtime.Parent, runtime.Selection)
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
    public async Task Composite_acceptance_receives_failed_child_evidence(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"parent context\"}",
            "{\"context\":\"child context\"}",
            "child output",
            "{\"verdict\":\"reject\",\"feedback\":\"child proof failed\"}",
            "{\"verdict\":\"accept\",\"evidence\":\"parent explicitly accepts failure\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var artifact = AgentTaskParser.ParseArtifact("""
            {"schema_version":1,"tasks":[{"name":"parent","description":"Parent","payload":[{"name":"child","description":"Child","payload":"child work","acceptance_criteria":"Child proof"}],"acceptance_criteria":"Parent decides"}]}
            """);

        var result = await new AgentTaskGraphRunner(registry, runtime.Router, runtime.Parent, runtime.Selection)
            .Run(artifact, cancellationToken);

        var task = result.Tasks.Single();
        _ = await Assert.That(task.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        _ = await Assert.That(task.Tasks?.Single().Status).IsEqualTo(AgentTaskExecutionStatus.Failed);
        var acceptance = provider.Requests[^1].Messages.Single(message => message.Role == LLMRole.User).Content;
        _ = await Assert.That(acceptance).Contains("Nested task results (structured JSON):");
        _ = await Assert.That(acceptance).Contains("child proof failed");
        _ = await Assert.That(acceptance).Contains("\"status\":\"failed\"");
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

        var result = await new AgentTaskGraphRunner(registry, runtime.Router, runtime.Parent, runtime.Selection)
            .Run(artifact, cancellationToken);

        _ = await Assert.That(result.Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
        _ = await Assert.That(provider.MaximumActive >= 2).IsTrue();
        _ = await Assert.That(provider.DependentStartedBeforeSlowFinished).IsTrue();
        _ = await Assert.That(string.Join(",", result.Tasks.Select(task => task.Name))).IsEqualTo("fast,slow,dependent");
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
        var running = new AgentTaskGraphRunner(registry, runtime.Router, runtime.Parent, runtime.Selection)
            .Run(artifact, canceled.Token);
        await provider.WaitUntilArrived(cancellationToken);

        await canceled.CancelAsync();
        _ = await Assert.That(running).Throws<OperationCanceledException>();

        _ = await Assert.That(registry.ActiveDirectChildren(runtime.Parent.SessionId)).IsEmpty();
    }

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
