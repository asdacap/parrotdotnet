using Parrot.Agent;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.Queues;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class AgentInventoryStreamTests
{
    [Test]
    [Timeout(30_000)]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Domain_publisher_captures_without_subscription_and_publishes_lifecycle_snapshots(
        bool queueDomain,
        CancellationToken cancellationToken)
    {
        if (!queueDomain && !OperatingSystem.IsLinux())
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var directory = Path.Combine(Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("n"));
        await using var fixture = await InventoryFixture.Create(directory, true);
        var root = fixture.Session.Registry.SnapshotScopes().Single();
        using var broker = new EventBroker();
        using var listener = broker.Subscribe();

        Task publication;
        IReadOnlyList<Event> captured;
        if (queueDomain)
        {
            var publisher = new QueueSnapshotPublisher(root.GetService<IAgentQueues>(), broker, root.Session.SessionId);
            captured = publisher.CaptureSnapshotEvents();
            _ = await Assert.That(listener.Reader.TryRead(out _)).IsFalse();
            publication = publisher.Run();
            var initial = await listener.Reader.ReadAsync(timeout.Token);
            _ = await Assert.That(initial.QueueSnapshot).IsNotNull();
            _ = await Assert.That(initial.QueueSnapshot.Queues).IsEmpty();
            _ = await Assert.That(listener.Reader.TryRead(out _)).IsFalse();

            _ = root.GetService<IAgentQueues>().Create("first", "first queue");
            _ = await root.GetService<IAgentQueues>().Push("first", ["item"], QueueDirection.Back, false, timeout.Token);
            var first = await ReadQueueBatch(listener, timeout.Token);
            _ = await Assert.That(first.SelectMany(item => item.QueueSnapshot.Queues).Select(queue => queue.Name).SequenceEqual(["first"])).IsTrue();
            _ = root.GetService<IAgentQueues>().Create("second", "second queue");
            _ = await root.GetService<IAgentQueues>().Push("second", ["item"], QueueDirection.Back, false, timeout.Token);
            var update = await ReadQueueBatch(listener, timeout.Token);
            _ = await Assert.That(update.SelectMany(item => item.QueueSnapshot.Queues).Select(queue => queue.Name).SequenceEqual(["first", "second"])).IsTrue();
        }
        else
        {
            var publisher = new ProcessSnapshotPublisher(root.GetService<IProcessOwner>(), broker);
            captured = publisher.CaptureSnapshotEvents();
            _ = await Assert.That(listener.Reader.TryRead(out _)).IsFalse();
            publication = publisher.Run();
            var initial = await listener.Reader.ReadAsync(timeout.Token);
            _ = await Assert.That(initial.ShellProcessSnapshot).IsNotNull();
            _ = await Assert.That(initial.ShellProcessSnapshot.Processes).IsEmpty();
            _ = await Assert.That(listener.Reader.TryRead(out _)).IsFalse();

            var security = fixture.Session.Mode.Profile.SecurityProfile;
            _ = root.GetService<IProcessOwner>().StartUnattributed("first", "sleep 30", ProcessEnvironmentOverrides.Empty, root.Session, security, ShellProcessTerminalMode.Pipe);
            var first = await ReadProcessBatch(listener, timeout.Token);
            _ = await Assert.That(first.SelectMany(item => item.ShellProcessSnapshot.Processes)).HasSingleItem();
            _ = root.GetService<IProcessOwner>().StartUnattributed("second", "sleep 30", ProcessEnvironmentOverrides.Empty, root.Session, security, ShellProcessTerminalMode.Pipe);
            var update = await ReadProcessBatch(listener, timeout.Token);
            _ = await Assert.That(update.SelectMany(item => item.ShellProcessSnapshot.Processes)).Count().IsEqualTo(2);
        }

        _ = await Assert.That(captured).HasSingleItem();
        _ = await Assert.That(listener.Reader.TryRead(out _)).IsFalse();
        await fixture.Session.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        await publication.WaitAsync(timeout.Token);
        Event removed;
        do
        {
            removed = await listener.Reader.ReadAsync(timeout.Token);
        }
        while (queueDomain
            ? !removed.QueueSnapshot.Removed
            : !removed.ShellProcessSnapshot.Removed);

        if (queueDomain)
        {
            _ = await Assert.That(removed.QueueSnapshot.Queues).IsEmpty();
        }
        else
        {
            _ = await Assert.That(removed.ShellProcessSnapshot.Processes).IsEmpty();
        }
    }

    [Test]
    [Timeout(30_000)]
    public async Task Listener_observes_updates_during_initial_capture_without_announcing_rejected_scopes(
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var directory = Path.Combine(Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("n"));
        await using var fixture = await InventoryFixture.Create(directory, false);
        var session = fixture.Session;
        var root = session.Registry.SnapshotScopes().Single();
        await using var rejected = fixture.CreateChild(root, "rejected");
        await root.ChildRegistry.DisposeChildren();
        _ = await Assert.That(root.ChildRegistry.TryAdd(rejected)).IsFalse();

        await using var listener = session.Listen(null, timeout.Token).GetAsyncEnumerator(timeout.Token);
        _ = await Assert.That(await listener.MoveNextAsync()).IsTrue();
        _ = await Assert.That(listener.Current.PayloadCase).IsEqualTo(Event.PayloadOneofCase.QueueSnapshot);
        var initialRevision = listener.Current.QueueSnapshot.Revision;
        _ = rejected.GetService<IAgentQueues>().Create("hidden", "rejected");
        _ = await rejected.GetService<IAgentQueues>().Push("hidden", ["item"], QueueDirection.Back, false, timeout.Token);
        await rejected.DisposeAsync();
        _ = root.GetService<IAgentQueues>().Create("during-capture", "race");
        _ = await root.GetService<IAgentQueues>().Push("during-capture", ["item"], QueueDirection.Back, false, timeout.Token);
        var latest = root.CaptureSnapshotEvents().Single(static published => published.QueueSnapshot is not null).QueueSnapshot;
        _ = await Assert.That(latest.Revision).IsGreaterThan(initialRevision);
        _ = await Assert.That(await listener.MoveNextAsync()).IsTrue();
        _ = await Assert.That(listener.Current.PayloadCase).IsEqualTo(Event.PayloadOneofCase.ShellProcessSnapshot);
        _ = await Assert.That(await listener.MoveNextAsync()).IsTrue();
        _ = await Assert.That(listener.Current.PayloadCase).IsEqualTo(Event.PayloadOneofCase.SessionUsageSnapshot);

        while (await listener.MoveNextAsync())
        {
            _ = await Assert.That(listener.Current.QueueSnapshot?.OwnerAgentSessionId).IsNotEqualTo(rejected.Session.SessionId);
            _ = await Assert.That(listener.Current.ShellProcessSnapshot?.OwnerAgentSessionId).IsNotEqualTo(rejected.Session.SessionId);
            if (listener.Current.QueueSnapshot?.Revision == latest.Revision)
            {
                _ = await Assert.That(listener.Current.QueueSnapshot).IsEqualTo(latest);
                break;
            }
        }

        await using var reconnected = session.Listen(null, timeout.Token).GetAsyncEnumerator(timeout.Token);
        _ = await Assert.That(await reconnected.MoveNextAsync()).IsTrue();
        _ = await Assert.That(reconnected.Current.QueueSnapshot).IsEqualTo(latest);
    }

    [Test]
    [Timeout(30_000)]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Listener_captures_admitted_owners_and_observes_later_admission_removal_and_reconnection(
        bool createBeforeListening,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var directory = Path.Combine(Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("n"));
        await using var fixture = await InventoryFixture.Create(directory, false);
        var session = fixture.Session;
        var root = session.Registry.SnapshotScopes().Single();
        root.PublishSnapshots();
        root.PublishSnapshots();
        _ = root.GetService<IAgentQueues>().Create("root-queue", "root");
        _ = await root.GetService<IAgentQueues>().Push("root-queue", ["item"], QueueDirection.Back, false, timeout.Token);
        await using var sibling = fixture.CreateChild(root, "sibling");
        _ = sibling.GetService<IAgentQueues>().Create("sibling-queue", "sibling");
        _ = await sibling.GetService<IAgentQueues>().Push("sibling-queue", ["item"], QueueDirection.Back, false, timeout.Token);
        _ = await Assert.That(root.ChildRegistry.TryAdd(sibling)).IsTrue();
        var earlyChild = createBeforeListening ? fixture.CreateChild(root, "later") : null;
        if (earlyChild is not null)
        {
            _ = earlyChild.GetService<IAgentQueues>().Create("child-queue", "child");
            _ = await earlyChild.GetService<IAgentQueues>().Push("child-queue", ["item"], QueueDirection.Back, false, timeout.Token);
        }

        await using var listener = session.Listen(null, timeout.Token).GetAsyncEnumerator(timeout.Token);
        var initial = await ReadInitial(listener);
        _ = await Assert.That(initial.Where(item => item.QueueSnapshot is not null)
            .Select(item => item.QueueSnapshot.OwnerAgentSessionId).SequenceEqual([root.Session.SessionId, sibling.Session.SessionId])).IsTrue();
        _ = await Assert.That(initial.Where(item => item.ShellProcessSnapshot is not null)
            .Select(item => item.ShellProcessSnapshot.OwnerAgentSessionId).SequenceEqual([root.Session.SessionId, sibling.Session.SessionId])).IsTrue();
        var rootSnapshot = initial.Single(item => item.QueueSnapshot?.OwnerAgentSessionId == root.Session.SessionId).QueueSnapshot;
        var siblingSnapshot = initial.Single(item => item.QueueSnapshot?.OwnerAgentSessionId == sibling.Session.SessionId).QueueSnapshot;
        _ = await Assert.That(rootSnapshot.Queues.Select(queue => queue.Name).SequenceEqual(["root-queue"])).IsTrue();
        _ = await Assert.That(siblingSnapshot.Queues.Select(queue => queue.Name).SequenceEqual(["sibling-queue"])).IsTrue();

        await using var child = earlyChild ?? fixture.CreateChild(root, "later");
        if (earlyChild is null)
        {
            _ = child.GetService<IAgentQueues>().Create("child-queue", "child");
            _ = await child.GetService<IAgentQueues>().Push("child-queue", ["item"], QueueDirection.Back, false, timeout.Token);
        }

        _ = await Assert.That(root.ChildRegistry.TryAdd(child)).IsTrue();
        var admitted = await ReadOwner(listener, child.Session.SessionId, false);
        var admittedQueues = admitted.Single(item => item.QueueSnapshot is not null).QueueSnapshot;
        var admittedProcesses = admitted.Single(item => item.ShellProcessSnapshot is not null).ShellProcessSnapshot;
        _ = await Assert.That(admittedQueues.Queues.Select(queue => queue.Name).SequenceEqual(["child-queue"])).IsTrue();
        _ = await Assert.That(admittedQueues.RootAgentSessionId).IsEqualTo(root.Session.SessionId);
        _ = await Assert.That(admittedQueues.InventoryInstanceId).IsNotEqualTo(siblingSnapshot.InventoryInstanceId);
        _ = await Assert.That(admittedProcesses.Processes).IsEmpty();

        _ = root.ChildRegistry.DetachDirectChildScope(child);
        await child.DisposeAsync();
        var removed = await ReadOwner(listener, child.Session.SessionId, true);
        var removedQueues = removed.Single(item => item.QueueSnapshot is not null).QueueSnapshot;
        var removedProcesses = removed.Single(item => item.ShellProcessSnapshot is not null).ShellProcessSnapshot;
        _ = await Assert.That(removedQueues.InventoryInstanceId).IsEqualTo(admittedQueues.InventoryInstanceId);
        _ = await Assert.That(removedQueues.Revision).IsGreaterThan(admittedQueues.Revision);
        _ = await Assert.That(removedQueues.Queues).IsEmpty();
        _ = await Assert.That(removedProcesses.InventoryInstanceId).IsEqualTo(admittedProcesses.InventoryInstanceId);
        _ = await Assert.That(removedProcesses.Revision).IsGreaterThan(admittedProcesses.Revision);
        _ = await Assert.That(removedProcesses.Processes).IsEmpty();
        _ = await Assert.That(removedProcesses.CompletedProcesses).IsEmpty();

        await listener.DisposeAsync();
        _ = sibling.GetService<IAgentQueues>().Create("after-disconnect", "reconnect");
        _ = await sibling.GetService<IAgentQueues>().Push("after-disconnect", ["item"], QueueDirection.Back, false, timeout.Token);
        using var reconnectCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var reconnected = session.Listen(null, reconnectCancellation.Token).GetAsyncEnumerator(reconnectCancellation.Token);
        var refreshed = await ReadInitial(reconnected);
        _ = await Assert.That(refreshed.Where(item => item.QueueSnapshot is not null)
            .Select(item => item.QueueSnapshot.OwnerAgentSessionId).SequenceEqual([root.Session.SessionId, sibling.Session.SessionId, sibling.Session.SessionId])).IsTrue();
        var siblingChunks = refreshed.Where(item => item.QueueSnapshot?.OwnerAgentSessionId == sibling.Session.SessionId)
            .Select(item => item.QueueSnapshot).ToArray();
        _ = await Assert.That(siblingChunks.SelectMany(snapshot => snapshot.Queues).Select(queue => queue.Name).SequenceEqual(["after-disconnect", "sibling-queue"])).IsTrue();
        _ = await Assert.That(siblingChunks.All(snapshot => snapshot.InventoryInstanceId == siblingSnapshot.InventoryInstanceId
            && snapshot.Revision > siblingSnapshot.Revision && !snapshot.Removed)).IsTrue();
        _ = await Assert.That(refreshed.Single(item => item.QueueSnapshot?.OwnerAgentSessionId == root.Session.SessionId)
            .QueueSnapshot).IsEqualTo(rootSnapshot);
        await reconnectCancellation.CancelAsync();
        _ = await Assert.That(async () =>
        {
            while (await reconnected.MoveNextAsync())
            {
            }
        }).Throws<OperationCanceledException>();
    }

    [Test]
    [Timeout(30_000)]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Disposing_scope_with_held_shell_claim_completes_local_inventory_streams(
        bool disposeRoot,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var directory = Path.Combine(Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("n"));
        await using var fixture = await InventoryFixture.Create(directory, true);
        var root = fixture.Session.Registry.SnapshotScopes().Single();
        await using var child = fixture.CreateChild(root, "held-claim");
        _ = await Assert.That(root.ChildRegistry.TryAdd(child)).IsTrue();
        var owner = disposeRoot ? root : child;
        _ = owner.GetService<IAgentQueues>().Create("queued", "held claim");
        _ = await owner.GetService<IAgentQueues>().Push("queued", ["item"], QueueDirection.Back, false, timeout.Token);
        var processOwner = owner.GetService<IProcessOwner>();
        var queueOwner = owner.GetService<IAgentQueues>();
        var process = processOwner.StartUnattributed("held", "sleep 300", ProcessEnvironmentOverrides.Empty, owner.Session, fixture.Session.Mode.Profile.SecurityProfile, ShellProcessTerminalMode.Pipe);
        using var queues = owner.GetService<IAgentQueues>().SubscribeInventory();
        using var processes = owner.GetService<IProcessOwner>().SubscribeInventory();
        var initialQueues = await queues.Reader.ReadAsync(timeout.Token);
        var initialProcesses = await processes.Reader.ReadAsync(timeout.Token);
        _ = await Assert.That(initialQueues.Queues).HasSingleItem();
        _ = await Assert.That(initialProcesses.Processes.Single().ProcessId).IsEqualTo(process.State.ProcessId);

        if (disposeRoot)
        {
            await fixture.Session.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        }
        else
        {
            _ = root.ChildRegistry.DetachDirectChildScope(child);
            await child.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        }

        var queueRevisions = new List<QueueInventorySnapshot>();
        await foreach (var snapshot in queues.Reader.ReadAllAsync(timeout.Token))
        {
            queueRevisions.Add(snapshot);
        }

        var processRevisions = new List<ShellProcessInventorySnapshot>();
        await foreach (var snapshot in processes.Reader.ReadAllAsync(timeout.Token))
        {
            processRevisions.Add(snapshot);
        }

        _ = await Assert.That(process.Completed).IsTrue();
        _ = await Assert.That(ReferenceEquals(owner.GetService<IProcessOwner>(), processOwner)).IsTrue();
        _ = await Assert.That(ReferenceEquals(owner.GetService<IAgentQueues>(), queueOwner)).IsTrue();
        _ = await Assert.That(queueRevisions.Last().Removed).IsTrue();
        _ = await Assert.That(queueRevisions.Last().Queues).IsEmpty();
        _ = await Assert.That(queueRevisions.Last().InventoryInstanceId).IsEqualTo(initialQueues.InventoryInstanceId);
        _ = await Assert.That(processRevisions.Last().Removed).IsTrue();
        _ = await Assert.That(processRevisions.Last().Processes).IsEmpty();
        _ = await Assert.That(processRevisions.Last().InventoryInstanceId).IsEqualTo(initialProcesses.InventoryInstanceId);
        _ = await Assert.That(owner.GetService<IAgentQueues>().CaptureInventory().Removed).IsTrue();
        _ = await Assert.That(owner.GetService<IProcessOwner>().CaptureInventory().Removed).IsTrue();
        _ = await Assert.That(child.GetService<IProcessOwner>().CaptureInventory().Removed).IsTrue();
        _ = await Assert.That(child.GetService<IAgentQueues>().CaptureInventory().Removed).IsTrue();
        _ = await Assert.That(() => owner.GetService<IProcessOwner>().StartUnattributed("late", "true", ProcessEnvironmentOverrides.Empty, owner.Session, fixture.Session.Mode.Profile.SecurityProfile, ShellProcessTerminalMode.Pipe))
            .Throws<InvalidOperationException>();
    }

    private static async Task<List<Event>> ReadQueueBatch(IEventSubscription listener, CancellationToken cancellationToken)
    {
        var batch = new List<Event> { await listener.Reader.ReadAsync(cancellationToken) };
        while (batch[^1].QueueSnapshot.FinalChunk is false)
        {
            batch.Add(await listener.Reader.ReadAsync(cancellationToken));
        }

        return batch;
    }

    private static async Task<List<Event>> ReadProcessBatch(IEventSubscription listener, CancellationToken cancellationToken)
    {
        var first = await listener.Reader.ReadAsync(cancellationToken);
        var batch = new List<Event> { first };
        while (batch.Count < first.ShellProcessSnapshot.ChunkCount)
        {
            batch.Add(await listener.Reader.ReadAsync(cancellationToken));
        }

        return batch;
    }

    private static async Task<List<Event>> ReadInitial(IAsyncEnumerator<Event> listener)
    {
        var snapshots = new List<Event>();
        while (await listener.MoveNextAsync())
        {
            if (listener.Current.SessionUsageSnapshot is not null)
            {
                return snapshots;
            }

            snapshots.Add(listener.Current);
        }

        throw new InvalidOperationException("Listener ended before its initial capture completed.");
    }

    private static async Task<List<Event>> ReadOwner(IAsyncEnumerator<Event> listener, string owner, bool removed)
    {
        var snapshots = new List<Event>();
        while (await listener.MoveNextAsync())
        {
            var published = listener.Current;
            if ((published.QueueSnapshot?.OwnerAgentSessionId == owner && published.QueueSnapshot.Removed == removed)
                || (published.ShellProcessSnapshot?.OwnerAgentSessionId == owner && published.ShellProcessSnapshot.Removed == removed))
            {
                snapshots.Add(published);
            }

            if (snapshots.Any(item => item.QueueSnapshot is not null) && snapshots.Any(item => item.ShellProcessSnapshot is not null))
            {
                return snapshots;
            }
        }

        throw new InvalidOperationException("Listener ended before both owner inventories arrived.");
    }

    private sealed class InventoryFixture(string directory, ProviderModel model, bool passThroughSandbox) : IAsyncDisposable
    {
        private readonly string _directory = directory;
        private readonly ProviderModel _model = model;
        private readonly ProductionSessionStoreFixture _production = new(directory, model, passThroughSandbox);
        private IUserSession? _session;

        public IUserSession Session => _session ?? throw new InvalidOperationException("The inventory session is not open.");

        public static async Task<InventoryFixture> Create(string directory, bool passThroughSandbox)
        {
            _ = Directory.CreateDirectory(directory);
            ILLMProvider provider = new UnusedProvider();
            var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
            var fixture = new InventoryFixture(directory, model, passThroughSandbox);
            try
            {
                fixture._session = await fixture._production.Store.Open(fixture._production.Router.Resolve(model.Selector));
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public IAgentSessionScope CreateChild(IAgentSessionScope parent, string name) => CreateIdentifiedChild(
            parent,
            AgentIdentity.Child(name, parent.Session.Identity, name, 1, AgentScope.Empty(_production.Configuration.PromptTemplates), AgentPolicyLineage.Root(), _production.Configuration.PromptTemplates));

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_session is not null)
                {
                    await _session.DisposeAsync();
                }
            }
            finally
            {
                _production.Dispose();
                Directory.Delete(_directory, recursive: true);
            }
        }

        private IAgentSessionScope CreateIdentifiedChild(IAgentSessionScope parent, AgentIdentity identity) => Session.Registry.CreateChildScope(
            identity,
            AgentSessionParentLink.Child(parent, AgentCompletionDeliveryPolicy.RetainedOnly, Session.Registry.ReserveRetainedAgent()),
            new ModelSelector(_model.Selector),
            Session.Mode.Profile,
            Session.Mode.Profile.SecurityProfile,
            Session.Registry.InitializeChildHistory(identity, new HistoryForkBoundary.AfterCompletedHistory(), HistoryForkSelection.Parse("empty"), new AgentHistorySource.Parent()),
            Session.Lifetime);
    }
}
