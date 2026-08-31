using Parrot.Agent;
using Parrot.Events;
using Parrot.Llm;
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
        var tool = new RunAgentTasksTool(new ToolWorkspace(_root), registry, runtime.Router, runtime.Parent);

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
        var tool = new RunAgentTasksTool(new ToolWorkspace(_root), registry, runtime.Router, runtime.Parent);
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
        var tool = new RunAgentTasksTool(new ToolWorkspace(_root), registry, runtime.Router, runtime.Parent);

        var result = await tool.Execute(
            new ToolInvocation("call", "{\"path\":\"alias.json\"}"),
            runtime.Selection,
            cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo("error: Path 'alias.json' traverses a symbolic link.");
    }

    [Test]
    public async Task Rejects_malformed_artifacts_after_secure_read(CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "artifact.json"), "{}", cancellationToken);
        var runtime = Runtime(cancellationToken);
        await using var registry = runtime.Registry;
        var tool = new RunAgentTasksTool(new ToolWorkspace(_root), registry, runtime.Router, runtime.Parent);

        var result = await tool.Execute(
            new ToolInvocation("call", "{\"path\":\"artifact.json\"}"),
            runtime.Selection,
            cancellationToken);

        _ = await Assert.That(result.Text).IsEqualTo("error: schema_version is required.");
    }

    private RuntimeContext Runtime(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([]);
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
            cancellationToken);
        var selected = parent.Selection();
        return new RuntimeContext(
            router,
            registry,
            parent,
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
        AgentTurnSelection Selection);
}
