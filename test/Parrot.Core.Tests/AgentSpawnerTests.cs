using Parrot.Agent;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
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
    [Arguments("blobs")]
    [Arguments("plan")]
    public async Task Rejects_reserved_child_names_and_extends_the_parent_name_path(
        string reservedName,
        CancellationToken cancellationToken)
    {
        await using var fixture = new SpawnerFixture(10, cancellationToken);

        _ = await Assert.That(() => fixture.Root.AgentSpawner.SpawnScope(fixture.Request with { RequestedName = reservedName }))
            .Throws<AgentRegistryException>();
        var child = fixture.Root.AgentSpawner.SpawnScope(fixture.Request);
        var grandchild = child.AgentSpawner.SpawnScope(fixture.Request with { Parent = child.Session, RequestedName = "leaf" });
        _ = await Assert.That(string.Join('/', child.Session.Identity.NamePath)).IsEqualTo("main/helper");
        _ = await Assert.That(string.Join('/', grandchild.Session.Identity.NamePath)).IsEqualTo("main/helper/leaf");
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

    [Test]
    [Arguments("!!!")]
    [Arguments("")]
    public async Task Spawn_or_resume_rejects_names_without_alphanumeric_characters(
        string requestedName,
        CancellationToken cancellationToken)
    {
        await using var fixture = new SpawnerFixture(2, cancellationToken);
        var request = fixture.Request with { RequestedName = requestedName };

        var rejected = await Assert.That(() => fixture.Root.AgentSpawner.SpawnOrResumeScope(request))
            .Throws<AgentRegistryException>();
        _ = await Assert.That(rejected?.Message).IsEqualTo("child agent name must contain at least one letter or digit");
        _ = await Assert.That(fixture.Root.ChildRegistry.SnapshotDescendants()).IsEmpty();
    }

    [Test]
    public async Task Spawn_or_resume_fails_while_the_named_child_is_running_and_resumes_once_idle(
        CancellationToken cancellationToken)
    {
        await using var fixture = new SpawnerFixture(2, cancellationToken);
        var request = fixture.Request with { RequestedName = "Busy Helper!" };
        var child = fixture.Root.AgentSpawner.SpawnOrResumeScope(request);
        var completion = child.Session.SendAndWaitForResult([ConversationPart.TextPart("first work")], Identifier.MessageId(), null, null, cancellationToken);
        await fixture.Provider.Arrived(cancellationToken);

        var busy = await Assert.That(() => fixture.Root.AgentSpawner.SpawnOrResumeScope(request))
            .Throws<AgentRegistryException>();
        _ = await Assert.That(busy?.Message).Contains($"child agent '{child.Session.Name}' is busy");
        _ = await Assert.That(fixture.Root.ChildRegistry.SnapshotDescendants()).HasSingleItem();

        fixture.Provider.Release();
        _ = await completion;

        var before = child.Session.CurrentSelection();
        var resumed = fixture.Root.AgentSpawner.SpawnOrResumeScope(request with
        {
            RequestedScope = "ignored scope",
            Fork = HistoryForkSelection.Parse("full"),
        });
        _ = await Assert.That(resumed).IsSameReferenceAs(child);
        _ = await Assert.That(resumed.Session.Identity.Scope).IsSameReferenceAs(child.Session.Identity.Scope);
        _ = await Assert.That(resumed.Session.CurrentSelection().RequestedModel).IsEqualTo(before.RequestedModel);
        _ = await Assert.That(fixture.Root.ChildRegistry.SnapshotDescendants()).HasSingleItem();
    }

    [Test]
    public async Task Spawn_or_resume_applies_a_new_model_and_profile_on_resume(CancellationToken cancellationToken)
    {
        await using var fixture = new SpawnerFixture(2, cancellationToken);
        var request = fixture.Request with { RequestedProfile = "worker", RequestedName = "helper" };
        var child = fixture.Root.AgentSpawner.SpawnOrResumeScope(request);
        var originalScope = child.Session.Identity.Scope;
        var firstWork = child.Session.SendAndWaitForResult([ConversationPart.TextPart("first work")], Identifier.MessageId(), null, null, cancellationToken);
        await fixture.Provider.Arrived(cancellationToken);
        fixture.Provider.Release();
        _ = await firstWork;

        var originalProfile = child.Session.CurrentSelection().Profile.Id;
        var request2 = fixture.Request with { RequestedProfile = "explorer", RequestedName = "helper" };
        var resumed = fixture.Root.AgentSpawner.SpawnOrResumeScope(request2);

        _ = await Assert.That(originalProfile).IsEqualTo("worker");
        _ = await Assert.That(resumed).IsSameReferenceAs(child);
        _ = await Assert.That(resumed.Session.CurrentSelection().Profile.Id).IsEqualTo("explorer");
        _ = await Assert.That(resumed.Session.Identity.Scope).IsSameReferenceAs(originalScope);
        _ = await Assert.That(resumed.ParentScope.DeliveryPolicy).IsEqualTo(AgentCompletionDeliveryPolicy.RetainedOnly);
    }

    [Test]
    public async Task Concurrent_spawn_or_resume_constructs_one_shared_session(CancellationToken cancellationToken)
    {
        await using var fixture = new SpawnerFixture(4, cancellationToken);
        var request = fixture.Request with { RequestedName = "SHARED..Helper" };
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable.Range(0, 8).Select(async _ =>
        {
            await start.Task.WaitAsync(cancellationToken);
            return fixture.Root.AgentSpawner.SpawnOrResumeScope(request);
        }).ToArray();
        start.SetResult();
        var children = await Task.WhenAll(callers);

        _ = await Assert.That(children.All(child => ReferenceEquals(child, children[0]))).IsTrue();
        _ = await Assert.That(fixture.Root.ChildRegistry.SnapshotDescendants()).HasSingleItem();
        _ = await Assert.That(children[0].Session.Name).IsEqualTo("shared-helper");
    }

    [Test]
    public async Task Sibling_history_inheritance_preserves_ownership_and_security_lineage(CancellationToken cancellationToken)
    {
        await using var fixture = new SpawnerFixture(10, cancellationToken);
        var source = fixture.Root.AgentSpawner.SpawnScope(fixture.Request with { RequestedName = "source" });
        fixture.Repository.AppendConversation(
            new Event { Id = "source-context", AgentSessionId = source.Session.SessionId },
            ConversationOrigin.UserInput,
            LLMRole.User,
            [ConversationPart.TextPart("dependency context")],
            [],
            string.Empty);
        fixture.Repository.AppendConversation(
            new Event { Id = "source-live", AgentSessionId = source.Session.SessionId },
            ConversationOrigin.Model,
            LLMRole.Assistant,
            [],
            [new LLMToolCall("live", "exec_command", "{}")],
            string.Empty);

        var child = fixture.Root.AgentSpawner.SpawnScope(fixture.Request with
        {
            RequestedName = "dependent",
            Fork = HistoryForkSelection.Parse("full"),
            HistorySource = new AgentHistorySource.Sibling(source.Session),
        });

        _ = await Assert.That(fixture.Repository.Conversation(child.Session.SessionId).Single().Parts.Single().Text)
            .IsEqualTo("dependency context");
        _ = await Assert.That(child.Session.ParentSessionId).IsEqualTo(fixture.Root.Session.SessionId);
        _ = await Assert.That(child.ParentScope.Parent).IsSameReferenceAs(fixture.Root);
        _ = await Assert.That(child.Session.Depth).IsEqualTo(source.Session.Depth);
        _ = await Assert.That(string.Join('/', child.Session.Identity.NamePath)).IsEqualTo("main/dependent");
        _ = await Assert.That(child.Session.ResolvePolicyLineage().CountProfile("worker"))
            .IsEqualTo(source.Session.ResolvePolicyLineage().CountProfile("worker"));
        foreach (var path in new[] { "/", "/tmp", "/tmp/dependency-artifact" })
        {
            _ = await Assert.That(child.Session.CurrentSelection().SecurityProfile.AllowsRead(path))
                .IsEqualTo(source.Session.CurrentSelection().SecurityProfile.AllowsRead(path));
            _ = await Assert.That(child.Session.CurrentSelection().SecurityProfile.AllowsWrite(path))
                .IsEqualTo(source.Session.CurrentSelection().SecurityProfile.AllowsWrite(path));
        }

        _ = await Assert.That(child.ParentScope.DeliveryPolicy).IsEqualTo(AgentCompletionDeliveryPolicy.RetainedOnly);
        _ = await Assert.That(source.ChildRegistry.SnapshotChildScopes()).IsEmpty();
    }

    [Test]
    public async Task Sibling_without_history_falls_back_to_the_owning_parent(CancellationToken cancellationToken)
    {
        await using var fixture = new SpawnerFixture(10, cancellationToken);
        fixture.Repository.AppendConversation(
            new Event { Id = "parent-context", AgentSessionId = fixture.Root.Session.SessionId },
            ConversationOrigin.UserInput,
            LLMRole.User,
            [ConversationPart.TextPart("parent context")],
            [],
            string.Empty);
        var source = fixture.Root.AgentSpawner.SpawnScope(fixture.Request with { RequestedName = "source" });
        var child = fixture.Root.AgentSpawner.SpawnScope(fixture.Request with
        {
            RequestedName = "dependent",
            Fork = HistoryForkSelection.Parse("full"),
            HistorySource = new AgentHistorySource.Sibling(source.Session),
        });

        _ = await Assert.That(fixture.Repository.Conversation(child.Session.SessionId).Single().Parts.Single().Text)
            .IsEqualTo("parent context");
        _ = await Assert.That(child.ParentScope.Parent).IsSameReferenceAs(fixture.Root);
    }

    [Test]
    public async Task Rejects_history_sources_from_other_owners_or_user_sessions(CancellationToken cancellationToken)
    {
        await using var fixture = new SpawnerFixture(10, cancellationToken);
        await using var otherSession = new SpawnerFixture(10, cancellationToken);
        var parent = fixture.Root.AgentSpawner.SpawnScope(fixture.Request with { RequestedName = "other-parent" });
        var otherBranch = parent.AgentSpawner.SpawnScope(fixture.Request with
        {
            Parent = parent.Session,
            RequestedName = "other-branch",
        });
        var foreign = otherSession.Root.AgentSpawner.SpawnScope(otherSession.Request);
        var before = fixture.Root.ChildRegistry.SnapshotDescendants().Count;
        foreach (var source in new[] { fixture.Root.Session, otherBranch.Session, foreign.Session })
        {
            _ = await Assert.That(() => fixture.Root.AgentSpawner.SpawnScope(fixture.Request with
            {
                RequestedName = "rejected",
                Fork = HistoryForkSelection.Parse("full"),
                HistorySource = new AgentHistorySource.Sibling(source),
            })).Throws<AgentRegistryException>()
                .WithMessage("history source must be a registered sibling under the child's owning parent");
            _ = await Assert.That(fixture.Root.ChildRegistry.SnapshotDescendants().Count).IsEqualTo(before);
        }

        var validSource = fixture.Root.AgentSpawner.SpawnScope(fixture.Request with { RequestedName = "valid-source" });
        var validChild = fixture.Root.AgentSpawner.SpawnScope(fixture.Request with
        {
            RequestedName = "rejected",
            HistorySource = new AgentHistorySource.Sibling(validSource.Session),
        });
        _ = await Assert.That(validChild.Session.Name).IsEqualTo("rejected");
    }

    private sealed class SpawnerFixture : IAsyncDisposable
    {
        private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");
        private readonly IEventBroker _broker = new EventBroker();
        private readonly SteppedProvider _provider;
        private readonly IAgentRegistry _registry;

        public SpawnerFixture(int retainedCapacity, CancellationToken cancellationToken)
        {
            _provider = Provider;
            Repository = new EventRepository(_database);
            var repository = Repository;
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
                profiles.Profile,
                profiles.Profile.SecurityProfile,
                _registry,
                cancellationToken);
            _registry.RegisterRootScope(Root);
            var selection = Root.Session.CurrentSelection();
            Request = new AgentLaunchRequest(
                Root.Session,
                new AgentTurnSelection(selection.RequestedModel, router.Resolve(selection.RequestedModel.Value), selection.Profile, selection.SecurityProfile),
                "worker",
                selection.RequestedModel,
                "helper",
                "original scope",
                HistoryForkSelection.Parse(string.Empty),
                new HistoryForkBoundary.AfterCompletedHistory(),
                AgentCompletionDeliveryPolicy.RetainedOnly,
                new AgentHistorySource.Parent());
        }

        public IEventRepository Repository { get; }

        public IAgentSessionScope Root { get; }

        public AgentLaunchRequest Request { get; }

        public SteppedProvider Provider { get; } = new();

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
