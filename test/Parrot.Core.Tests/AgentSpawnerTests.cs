using Parrot.Agent;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class AgentSpawnerTests
{
    [Test]
    [Arguments("Helper", "helper")]
    [Arguments("  HELPER...Name!!", "helper-name")]
    public async Task Reuses_normalized_direct_child_unchanged_while_regular_spawns_suffix(
        string requestedName,
        string normalizedName,
        CancellationToken cancellationToken)
    {
        await using var fixture = new SpawnerFixture(10, cancellationToken);
        var request = fixture.Request with { RequestedName = requestedName };
        var original = fixture.Root.AgentSpawner.SpawnScope(request);
        var originalSelection = original.Session.CurrentSelection();
        var originalScope = original.Session.Identity.Scope;
        var reused = fixture.Root.AgentSpawner.GetOrSpawnScope(normalizedName, () =>
            throw new InvalidOperationException("Launch selection must not run for an existing child."));
        var sibling = fixture.Root.AgentSpawner.SpawnScope(request);
        var otherParent = fixture.Root.AgentSpawner.SpawnScope(request with { RequestedName = "other-parent" });
        var otherBranch = otherParent.AgentSpawner.GetOrSpawnScope(requestedName, () => request with
        {
            Parent = otherParent.Session,
        });

        _ = await Assert.That(reused).IsSameReferenceAs(original);
        _ = await Assert.That(reused.Session.CurrentSelection()).IsEqualTo(originalSelection);
        _ = await Assert.That(reused.Session.Identity.Scope).IsSameReferenceAs(originalScope);
        _ = await Assert.That(reused.Session.Name).IsEqualTo(normalizedName);
        _ = await Assert.That(sibling.Session.Name).IsEqualTo($"{normalizedName}-2");
        _ = await Assert.That(otherBranch.Session.Name).IsEqualTo(normalizedName);
        _ = await Assert.That(otherBranch.Session.ParentSessionId).IsEqualTo(otherParent.Session.SessionId);
        _ = await Assert.That(otherBranch.Session.SessionId).IsNotEqualTo(original.Session.SessionId);
    }

    [Test]
    public async Task Concurrent_reuse_constructs_once_and_consumes_one_retained_reservation(
        CancellationToken cancellationToken)
    {
        await using var fixture = new SpawnerFixture(1, cancellationToken);
        var selections = 0;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable.Range(0, 16).Select(async index =>
        {
            await start.Task.WaitAsync(cancellationToken);
            return fixture.Root.AgentSpawner.GetOrSpawnScope(index % 2 == 0 ? "HELPER!!" : "helper", () =>
            {
                _ = Interlocked.Increment(ref selections);
                return fixture.Request;
            });
        }).ToArray();
        start.SetResult();
        var children = await Task.WhenAll(callers);

        _ = await Assert.That(selections).IsEqualTo(1);
        _ = await Assert.That(children.All(child => ReferenceEquals(child, children[0]))).IsTrue();
        _ = await Assert.That(fixture.Root.ChildRegistry.SnapshotDescendants()).HasSingleItem();
        var rejected = await Assert.That(() => fixture.Root.AgentSpawner.SpawnScope(
            fixture.Request with { RequestedName = "another-child" })).Throws<AgentRegistryException>();
        _ = await Assert.That(rejected?.Message).IsEqualTo("subagent retention limit reached");
        var reused = fixture.Root.AgentSpawner.GetOrSpawnScope("helper", () =>
            throw new InvalidOperationException("An exhausted budget must not prevent reuse."));
        _ = await Assert.That(reused).IsSameReferenceAs(children[0]);
    }

    [Test]
    [Arguments("!!!")]
    [Arguments("")]
    public async Task Names_without_alphanumeric_characters_keep_generated_fallback(
        string requestedName,
        CancellationToken cancellationToken)
    {
        await using var fixture = new SpawnerFixture(2, cancellationToken);
        var request = fixture.Request with { RequestedName = requestedName };
        var first = fixture.Root.AgentSpawner.GetOrSpawnScope(requestedName, () => request);
        var second = fixture.Root.AgentSpawner.GetOrSpawnScope(requestedName, () => request);

        _ = await Assert.That(first.Session.Name).IsEqualTo($"agent-{first.Session.SessionId[^6..]}");
        _ = await Assert.That(second.Session.Name).IsEqualTo($"agent-{second.Session.SessionId[^6..]}");
        _ = await Assert.That(first.Session.SessionId).IsNotEqualTo(second.Session.SessionId);
        _ = await Assert.That(fixture.Root.ChildRegistry.SnapshotDescendants()).Count().IsEqualTo(2);
    }

    private sealed class SpawnerFixture : IAsyncDisposable
    {
        private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");
        private readonly EventBroker _broker = new();
        private readonly SteppedProvider _provider = new();
        private readonly IAgentRegistry _registry;

        public SpawnerFixture(int retainedCapacity, CancellationToken cancellationToken)
        {
            var repository = new EventRepository(_database);
            var router = TestModels.Route(new ProviderModel(_provider, new LLMModel("model", _provider.Id)));
            var sessions = new AgentTaskTestSessionFactory(router);
            var profiles = new TestProfileFixture();
            _registry = TestModels.RegistryWithBudget(
                sessions,
                _broker,
                repository,
                profiles.Registry,
                TestModels.PromptTemplates,
                new RetainedAgentBudget(retainedCapacity),
                cancellationToken);
            Root = sessions.Create(
                AgentIdentity.Main("root", "main", TestModels.PromptTemplates),
                AgentSessionParentLink.Root(),
                new ModelSelector($"{_provider.Id}/model"),
                _broker,
                repository,
                profiles.Mode,
                profiles.Mode.Profile.SecurityProfile,
                _registry.RequireStatus(),
                _registry,
                cancellationToken);
            _registry.RegisterRootScope(Root);
            var selection = Root.Session.CurrentSelection();
            Request = new AgentLaunchRequest(
                Root.Session,
                new AgentTurnSelection(selection.RequestedModel, router.Resolve(selection.RequestedModel.Value), selection.Mode, selection.SecurityProfile),
                "worker",
                selection.RequestedModel,
                "helper",
                "original scope",
                HistoryForkSelection.Parse(string.Empty),
                new HistoryForkBoundary.AfterCompletedHistory(),
                AgentCompletionDeliveryPolicy.RetainedOnly);
        }

        public IAgentSessionScope Root { get; }

        public AgentLaunchRequest Request { get; }

        public async ValueTask DisposeAsync()
        {
            await _registry.DisposeAsync();
            TestModels.UnregisterScope(Root);
            await Root.DisposeAsync();
            _provider.Dispose();
            _broker.Dispose();
            _database.Dispose();
        }
    }
}
