using Parrot.Agent;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Security;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class RunAgentTasksToolTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parrot-run-agent-tasks-tool-tests", Guid.NewGuid().ToString("N"));

    private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");

    private readonly EventBroker _broker = new();

    public RunAgentTasksToolTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _broker.Dispose();
        _database.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Test]
    [Arguments("{}", "error: Tool arguments require a string 'path'.")]
    [Arguments("{\"path\":\"\"}", "error: Tool arguments require a nonblank string 'path'.")]
    [Arguments("{\"path\":\"missing.json\"}", "error: Source 'missing.json' is missing.")]
    public async Task Rejects_invalid_or_missing_paths(
        string arguments,
        string expected,
        CancellationToken cancellationToken)
    {
        var runtime = Runtime(cancellationToken);
        await using var registry = runtime.Registry;
        var tool = Tool(registry, runtime);

        var result = await tool.Execute(new ToolInvocation("call", arguments), runtime.Selection, cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo(expected);
    }

    [Test]
    public async Task Rejects_out_of_policy_artifacts(CancellationToken cancellationToken)
    {
        var artifact = Path.Combine(_root, "denied.json");
        await File.WriteAllTextAsync(artifact, "{}", cancellationToken);
        var runtime = Runtime(cancellationToken);
        await using var registry = runtime.Registry;
        var tool = Tool(registry, runtime);
        var denied = runtime.Selection with
        {
            SecurityProfile = SecurityProfile.Compose(
                false,
                [],
                [new SandboxRule(artifact, SandboxRuleAction.DenyRead)],
                []),
        };

        var result = await tool.Execute(
            new ToolInvocation("call", "{\"path\":\"denied.json\"}"),
            denied,
            cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo("error: access denied");
    }

    [Test]
    public async Task Rejects_symbolic_link_artifacts(CancellationToken cancellationToken)
    {
        var artifact = Path.Combine(_root, "artifact.json");
        await File.WriteAllTextAsync(artifact, "{}", cancellationToken);
        _ = File.CreateSymbolicLink(Path.Combine(_root, "alias.json"), artifact);
        var runtime = Runtime(cancellationToken);
        await using var registry = runtime.Registry;
        var tool = Tool(registry, runtime);

        var result = await tool.Execute(
            new ToolInvocation("call", "{\"path\":\"alias.json\"}"),
            runtime.Selection,
            cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo("error: Path 'alias.json' traverses a symbolic link.");
    }

    [Test]
    public async Task Persists_and_publishes_progress_for_successful_invocation(CancellationToken cancellationToken)
    {
        const string artifactJson =
            """
            {"schema_version":1,"tasks":[{"name":"leaf","description":"Leaf","payload":"work","acceptance_criteria":"Done"}]}
            """;
        await File.WriteAllTextAsync(
            Path.Combine(_root, "artifact.json"),
            artifactJson,
            cancellationToken);
        var provider = new AgentTaskQueueProvider([
            "{\"context\":\"ready\"}",
            "executed",
            "{\"verdict\":\"accept\",\"evidence\":\"done\"}",
        ]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        using var subscription = _broker.Subscribe();
        var tool = Tool(registry, runtime);
        var running = tool.Execute(
            new ToolInvocation("distinctive-call", "{\"path\":\"artifact.json\"}"),
            runtime.Selection,
            cancellationToken);

        var brokered = await ObserveProgress(
            subscription,
            runtime.Repository,
            "distinctive-call",
            3,
            cancellationToken);
        var result = await running;

        using var document = System.Text.Json.JsonDocument.Parse(result.Text);
        _ = await Assert.That(document.RootElement.GetProperty("status").GetString()).IsEqualTo("succeeded");
        _ = await Assert.That(document.RootElement.GetProperty("tasks")[0].GetProperty("name").GetString())
            .IsEqualTo("leaf");
        var durable = runtime.Repository.Replay()
            .Where(published => published.PayloadCase == Event.PayloadOneofCase.AgentTaskProgressSnapshot)
            .ToArray();
        _ = await Assert.That(string.Join(',', durable.Select(published => published.AgentTaskProgressSnapshot.Revision)))
            .IsEqualTo("1,2,3");
        _ = await Assert.That(durable.All(published =>
            published.AgentSessionId == runtime.Parent.SessionId
            && published.AgentTaskProgressSnapshot.OriginToolCallId == "distinctive-call"))
            .IsTrue();
        _ = await Assert.That(brokered).Count().IsEqualTo(3);
    }

    [Test]
    public async Task Rejects_malformed_artifacts_after_secure_read(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "artifact.json"), "{}", cancellationToken);
        var runtime = Runtime(cancellationToken);
        await using var registry = runtime.Registry;
        var tool = Tool(registry, runtime);

        var result = await tool.Execute(
            new ToolInvocation("call", "{\"path\":\"artifact.json\"}"),
            runtime.Selection,
            cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo("error: schema_version is required.");
    }

    private static async Task<Event[]> ObserveProgress(
        EventSubscription subscription,
        EventRepository repository,
        string originToolCallId,
        int count,
        CancellationToken cancellationToken)
    {
        var observed = new List<Event>(count);
        while (observed.Count < count)
        {
            var published = await subscription.Reader.ReadAsync(cancellationToken);
            if (published.PayloadCase != Event.PayloadOneofCase.AgentTaskProgressSnapshot
                || published.AgentTaskProgressSnapshot.OriginToolCallId != originToolCallId)
            {
                continue;
            }

            if (!repository.Replay().Any(committed => committed.Id == published.Id))
            {
                throw new InvalidOperationException("Broker published AgentTask progress before it was durable.");
            }

            observed.Add(published);
        }

        return [.. observed];
    }

    private RunAgentTasksTool Tool(AgentRegistry registry, RuntimeContext runtime) => new(
        new ToolWorkspace(_root),
        registry,
        runtime.Router,
        runtime.Parent,
        _broker,
        runtime.Repository);

    private RuntimeContext Runtime(CancellationToken cancellationToken) =>
        Runtime(new AgentTaskQueueProvider([]), cancellationToken);

    private RuntimeContext Runtime(AgentTaskQueueProvider provider, CancellationToken cancellationToken)
    {
        var model = new LLMModel("model", provider.Id);
        var providers = new ProviderRegistry(
            [provider],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal) { [provider.Id] = [model] });
        var router = new ModelRouter(providers, new ModelAliasCatalog(providers, []), $"{provider.Id}/model");
        var repository = new EventRepository(_database);
        var sessions = new AgentTaskTestSessionFactory(router);
        var registry = TestModels.Registry(sessions, _broker, repository, TestModels.ProfileRegistry(), cancellationToken);
        var identity = AgentIdentity.Main("tool-parent", "parent");
        var dependencies = TestModels.Dependencies(identity, _broker, repository, cancellationToken);
        var parent = new AgentSession(
            identity,
            new ModelSelector($"{provider.Id}/model"),
            router,
            _broker,
            repository,
            [],
            TestModels.EmptyToolDefinitions,
            TestModels.MaterializePrompt(identity, _root, _root),
            new TodoCollection(identity.SessionId, repository, _broker),
            new ToolOutputBlobStore(_root),
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
        return new RuntimeContext(
            router,
            registry,
            parent,
            repository,
            new AgentTurnSelection(
                selected.RequestedModel,
                router.Resolve(selected.RequestedModel.Value),
                selected.Profile,
                selected.SecurityProfile));
    }

    private sealed record RuntimeContext(
        ModelRouter Router,
        AgentRegistry Registry,
        AgentSession Parent,
        EventRepository Repository,
        AgentTurnSelection Selection);
}
