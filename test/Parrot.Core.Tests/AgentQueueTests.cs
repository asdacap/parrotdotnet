using Parrot.Agent;
using Parrot.Queues;
using Parrot.State;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class AgentQueueTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parrot-agent-queue-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task An_agent_accesses_its_own_and_direct_parents_queues(CancellationToken cancellationToken)
    {
        var resources = Resources("direct-access");
        using var catalog = new AgentQueueCatalog(resources);
        using var root = catalog.Register(AgentIdentity.Main("root-agent", "root"));
        using var child = catalog.Register(Child("child-agent", "root-agent", "root", "child", 1));
        var parentQueue = root.Create("parent-work", "parent owned");
        var childQueue = child.Create("child-work", "child owned");

        _ = await root.Push("parent-work", ["parent-item"], QueueDirection.Back, false, cancellationToken);
        _ = await child.Push("child-work", ["child-item"], QueueDirection.Back, false, cancellationToken);
        _ = await child.Push("parent-work", ["from-child"], QueueDirection.Back, false, cancellationToken);
        _ = await child.Listen("parent-work", enabled: true, cancellationToken);

        _ = await Assert.That(child.Get("child-work").Path).IsEqualTo(childQueue.Path);
        _ = await Assert.That(child.Get("parent-work").Path).IsEqualTo(parentQueue.Path);
        _ = await Assert.That(child.Get("parent-work").Size).IsEqualTo(2);
        _ = await Assert.That(child.Get("parent-work").Monitored).IsTrue();
        _ = await Assert.That(root.Get("parent-work").Monitored).IsFalse();
        _ = await Assert.That(string.Join(',', child.List().Select(static queue => queue.Name)))
            .IsEqualTo("child-work,parent-work");

        var own = child.TryTake("child-work", 1, QueueDirection.Front);
        var inherited = child.TryTake("parent-work", 2, QueueDirection.Front);
        _ = await Assert.That(string.Join(',', own.Items)).IsEqualTo("child-item");
        _ = await Assert.That(string.Join(',', inherited.Items)).IsEqualTo("parent-item,from-child");
        _ = await Assert.That(root.Get("parent-work").Size).IsEqualTo(0);
    }

    [Test]
    public async Task A_direct_child_can_close_and_drain_its_parents_queue(CancellationToken cancellationToken)
    {
        using var catalog = new AgentQueueCatalog(Resources("child-close"));
        using var root = catalog.Register(AgentIdentity.Main("root-agent", "root"));
        using var child = catalog.Register(Child("child-agent", "root-agent", "root", "child", 1));
        _ = root.Create("parent-work", "shared");
        _ = await root.Push("parent-work", ["final-item"], QueueDirection.Back, false, cancellationToken);
        _ = await child.Listen("parent-work", true, cancellationToken);

        var closed = await child.Push("parent-work", [], QueueDirection.Back, true, cancellationToken);
        var taken = child.TryTake("parent-work", 1, QueueDirection.Front);
        var completed = child.TryTake("parent-work", 1, QueueDirection.Front);

        _ = await Assert.That(closed.Closed).IsTrue();
        _ = await Assert.That(closed.Monitored).IsTrue();
        _ = await Assert.That(root.Get("parent-work").Closed).IsTrue();
        _ = await Assert.That(root.Push("parent-work", ["late"], QueueDirection.Back, false, cancellationToken))
            .Throws<QueueClosedException>()
            .WithMessage("queue: 'parent-work' is closed");
        _ = await Assert.That(string.Join(',', taken.Items)).IsEqualTo("final-item");
        _ = await Assert.That(taken.Info?.Closed).IsTrue();
        _ = await Assert.That(completed.Acquired).IsTrue();
        _ = await Assert.That(completed.Items).IsEmpty();
        _ = await Assert.That(completed.Info?.Monitored).IsTrue();
    }

    [Test]
    public async Task Reverse_child_sibling_and_grandparent_access_is_denied_without_leaking_ownership()
    {
        using var catalog = new AgentQueueCatalog(Resources("denied-access"));
        using var root = catalog.Register(AgentIdentity.Main("root-agent", "root"));
        using var left = catalog.Register(Child("left-agent", "root-agent", "root", "left", 1));
        using var right = catalog.Register(Child("right-agent", "root-agent", "root", "right", 1));
        using var grandchild = catalog.Register(Child("grandchild-agent", "left-agent", "left", "grandchild", 2));
        _ = root.Create("root-secret", string.Empty);
        _ = left.Create("left-secret", string.Empty);
        _ = grandchild.Create("grandchild-secret", string.Empty);

        var reverseChild = CaptureNotFound(() => root.Get("left-secret"));
        var sibling = CaptureNotFound(() => right.Get("left-secret"));
        var grandparent = CaptureNotFound(() => grandchild.Get("root-secret"));
        var reverseDescendant = CaptureNotFound(() => root.Get("grandchild-secret"));
        var absent = CaptureNotFound(() => right.Get("never-created"));

        _ = await Assert.That(reverseChild.Message).IsEqualTo("queue: 'left-secret' was not found");
        _ = await Assert.That(sibling.Message).IsEqualTo(reverseChild.Message);
        _ = await Assert.That(grandparent.Message).IsEqualTo("queue: 'root-secret' was not found");
        _ = await Assert.That(reverseDescendant.Message).IsEqualTo("queue: 'grandchild-secret' was not found");
        _ = await Assert.That(absent.Message).IsEqualTo("queue: 'never-created' was not found");
        _ = await Assert.That(string.Join(',', grandchild.List().Select(static queue => queue.Name)))
            .IsEqualTo("grandchild-secret,left-secret");
        _ = await Assert.That(string.Join(',', right.List().Select(static queue => queue.Name)))
            .IsEqualTo("root-secret");
    }

    [Test]
    public async Task Parent_child_name_collisions_are_rejected_in_both_create_orders()
    {
        using var catalog = new AgentQueueCatalog(Resources("collision-orders"));
        using var root = catalog.Register(AgentIdentity.Main("root-agent", "root"));
        using var child = catalog.Register(Child("child-agent", "root-agent", "root", "child", 1));

        _ = root.Create("parent-first", string.Empty);
        _ = await Assert.That(() => child.Create("parent-first", string.Empty))
            .Throws<QueueAlreadyExistsException>();

        _ = child.Create("child-first", string.Empty);
        _ = await Assert.That(() => root.Create("child-first", string.Empty))
            .Throws<QueueAlreadyExistsException>();

        _ = await Assert.That(root.Get("parent-first").Path).IsEqualTo(child.Get("parent-first").Path);
        _ = await Assert.That(() => root.Get("child-first")).Throws<QueueNotFoundException>();
    }

    [Test]
    public async Task Concurrent_parent_child_creation_publishes_exactly_one_queue()
    {
        var resources = Resources("concurrent-collision");
        using var catalog = new AgentQueueCatalog(resources);
        using var root = catalog.Register(AgentIdentity.Main("root-agent", "root"));
        using var child = catalog.Register(Child("child-agent", "root-agent", "root", "child", 1));
        using var start = new ManualResetEventSlim();
        var parentAttempt = Task.Run(() => TryCreate(root, "same-name", start));
        var childAttempt = Task.Run(() => TryCreate(child, "same-name", start));

        start.Set();
        var outcomes = await Task.WhenAll(parentAttempt, childAttempt);
        var physicalQueues = Directory.EnumerateFiles(resources.QueueDirectory, "same-name.jsonl", SearchOption.AllDirectories)
            .ToList();

        _ = await Assert.That(outcomes.Count(static created => created)).IsEqualTo(1);
        _ = await Assert.That(physicalQueues).Count().IsEqualTo(1);
        _ = await Assert.That(child.Get("same-name").Path).IsEqualTo(physicalQueues[0]);
    }

    [Test]
    public async Task Siblings_can_use_the_same_name_without_sharing_data(CancellationToken cancellationToken)
    {
        using var catalog = new AgentQueueCatalog(Resources("sibling-isolation"));
        using var root = catalog.Register(AgentIdentity.Main("root-agent", "root"));
        using var left = catalog.Register(Child("left-agent", "root-agent", "root", "left", 1));
        using var right = catalog.Register(Child("right-agent", "root-agent", "root", "right", 1));
        var leftInfo = left.Create("shared-name", "left queue");
        var rightInfo = right.Create("shared-name", "right queue");

        _ = await left.Push("shared-name", ["left-item"], QueueDirection.Back, false, cancellationToken);
        _ = await right.Push("shared-name", ["right-item"], QueueDirection.Back, false, cancellationToken);
        var leftTaken = left.TryTake("shared-name", 1, QueueDirection.Front);
        var rightTaken = right.TryTake("shared-name", 1, QueueDirection.Front);

        _ = await Assert.That(leftInfo.Path).IsNotEqualTo(rightInfo.Path);
        _ = await Assert.That(string.Join(',', leftTaken.Items)).IsEqualTo("left-item");
        _ = await Assert.That(string.Join(',', rightTaken.Items)).IsEqualTo("right-item");
        _ = await Assert.That(() => root.Get("shared-name")).Throws<QueueNotFoundException>();
        _ = await Assert.That(() => root.Create("shared-name", string.Empty))
            .Throws<QueueAlreadyExistsException>();
    }

    [Test]
    public async Task Disposing_a_child_removes_only_its_queue_directory()
    {
        var resources = Resources("child-cleanup");
        using var catalog = new AgentQueueCatalog(resources);
        using var root = catalog.Register(AgentIdentity.Main("root-agent", "root"));
        var child = catalog.Register(Child("child-agent", "root-agent", "root", "child", 1));
        var childDirectory = resources.AgentQueueDirectory("child-agent");
        _ = child.Create("temporary-work", string.Empty);
        _ = root.Create("root-work", string.Empty);

        child.Dispose();

        _ = await Assert.That(Directory.Exists(childDirectory)).IsFalse();
        _ = await Assert.That(File.Exists(root.Get("root-work").Path)).IsTrue();
        using var replacement = catalog.Register(Child("child-agent", "root-agent", "root", "child", 1));
        _ = await Assert.That(string.Join(',', replacement.List().Select(static queue => queue.Name)))
            .IsEqualTo("root-work");
        _ = replacement.Create("fresh-work", string.Empty);
        _ = await Assert.That(Directory.Exists(childDirectory)).IsTrue();
    }

    [Test]
    public async Task Catalog_startup_removes_stale_agent_queues_without_removing_root_queues()
    {
        var resources = Resources("stale-cleanup");
        _ = Directory.CreateDirectory(resources.QueueDirectory);
        using (var persisted = new QueueStore(resources.QueueDirectory))
        {
            _ = persisted.Create("root-work", "persistent");
        }

        var staleDirectory = Directory.CreateDirectory(
            Path.Combine(resources.AgentQueueRootDirectory, "stale-agent", "nested")).FullName;
        await File.WriteAllTextAsync(Path.Combine(staleDirectory, "orphaned.jsonl"), "orphaned");

        using var catalog = new AgentQueueCatalog(resources);

        _ = await Assert.That(Directory.Exists(resources.AgentQueueRootDirectory)).IsFalse();
        using var root = catalog.Register(AgentIdentity.Main("root-agent", "root"));
        _ = await Assert.That(root.Get("root-work").Description).IsEqualTo("persistent");
    }

    [Test]
    public async Task Root_queues_persist_across_catalog_lifetimes(CancellationToken cancellationToken)
    {
        var resources = Resources("root-persistence");
        using (var firstCatalog = new AgentQueueCatalog(resources))
        {
            using var firstRoot = firstCatalog.Register(AgentIdentity.Main("first-root", "root"));
            _ = firstRoot.Create("persistent-work", "survives");
            _ = await firstRoot.Push("persistent-work", ["item"], QueueDirection.Back, false, cancellationToken);
        }

        using var secondCatalog = new AgentQueueCatalog(resources);
        using var secondRoot = secondCatalog.Register(AgentIdentity.Main("second-root", "root"));
        var restored = secondRoot.Get("persistent-work");

        _ = await Assert.That(restored.Description).IsEqualTo("survives");
        _ = await Assert.That(restored.Size).IsEqualTo(1);
        _ = await Assert.That(Path.GetDirectoryName(restored.Path)).IsEqualTo(resources.QueueDirectory);
    }

    [Test]
    public async Task Agent_queue_directories_are_canonical_and_contained()
    {
        var resources = Resources("contained-paths");
        var directory = resources.AgentQueueDirectory("agent-session-child");

        _ = await Assert.That(directory)
            .IsEqualTo(Path.GetFullPath(Path.Combine(resources.AgentQueueRootDirectory, "agent-session-child")));
        _ = await Assert.That(resources.Owns(directory)).IsTrue();
        _ = await Assert.That(() => resources.AgentQueueDirectory(Path.Combine("..", "escaped")))
            .Throws<ArgumentException>();
        _ = await Assert.That(() => resources.AgentQueueDirectory(".."))
            .Throws<ArgumentException>();
        _ = await Assert.That(() => resources.AgentQueueDirectory(Path.GetPathRoot(resources.Root) ?? resources.Root))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Disposing_a_child_releases_its_parent_listener_and_delivery_reservation(
        CancellationToken cancellationToken)
    {
        using var catalog = new AgentQueueCatalog(Resources("listener-cleanup"));
        using var root = catalog.Register(AgentIdentity.Main("root-agent", "root"));
        var child = catalog.Register(Child("child-agent", "root-agent", "root", "child", 1));
        _ = root.Create("parent-work", string.Empty);
        _ = root.Local.Monitor("parent-work", child.SessionId, true);
        _ = root.Local.Monitor("parent-work", "other-agent", true);
        _ = root.Local.Push("parent-work", ["item"], QueueDirection.Back, false);
        var rejected = await root.Local.DeliverMonitored(
            child.SessionId,
            static (_, _) => Task.FromResult(false),
            cancellationToken);

        child.Dispose();
        var accepted = await root.Local.DeliverMonitored(
            "other-agent",
            static (_, _) => Task.FromResult(true),
            cancellationToken);

        _ = await Assert.That(rejected).IsFalse();
        _ = await Assert.That(accepted).IsTrue();
        _ = await Assert.That(string.Join(',', root.Local.ListenerSessionIds("parent-work")))
            .IsEqualTo("other-agent");
        _ = await Assert.That(root.Get("parent-work").Size).IsEqualTo(0);
    }

    [Test]
    public async Task Empty_take_preserves_the_invoking_agents_listener_state(CancellationToken cancellationToken)
    {
        using var catalog = new AgentQueueCatalog(Resources("empty-listener"));
        using var root = catalog.Register(AgentIdentity.Main("root-agent", "root"));
        _ = root.Create("empty-work", string.Empty);
        _ = await root.Listen("empty-work", true, cancellationToken);

        QueueEmptyException? empty = null;
        try
        {
            _ = root.TryTake("empty-work", 1, QueueDirection.Front);
        }
        catch (QueueEmptyException failure)
        {
            empty = failure;
        }

        _ = await Assert.That(empty).IsNotNull();
        _ = await Assert.That(empty?.Info?.Monitored).IsTrue();
    }

    [Test]
    public async Task A_canceled_push_does_not_mutate_the_queue()
    {
        using var catalog = new AgentQueueCatalog(Resources("canceled-push"));
        using var root = catalog.Register(AgentIdentity.Main("root-agent", "root"));
        _ = root.Create("work", string.Empty);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        _ = await Assert.That(root.Push("work", ["item"], QueueDirection.Back, false, canceled.Token))
            .Throws<OperationCanceledException>();
        _ = await Assert.That(root.Get("work").Size).IsEqualTo(0);
    }

    [Test]
    public async Task Inventory_aggregates_all_owners_and_removes_a_disposed_child(CancellationToken cancellationToken)
    {
        using var catalog = new AgentQueueCatalog(Resources("aggregate-inventory"));
        using var root = catalog.Register(AgentIdentity.Main("root-agent", "root"));
        var left = catalog.Register(Child("left-agent", "root-agent", "root", "left", 1));
        using var right = catalog.Register(Child("right-agent", "root-agent", "root", "right", 1));
        _ = root.Create("root-work", "root");
        _ = left.Create("shared-name", "left");
        _ = right.Create("shared-name", "right");
        using var subscription = catalog.SubscribeInventory();
        var initial = await subscription.Reader.ReadAsync(cancellationToken);

        _ = await root.Push("root-work", ["root-item"], QueueDirection.Back, false, cancellationToken);
        _ = await left.Push("shared-name", ["left-item"], QueueDirection.Back, false, cancellationToken);
        _ = await right.Push("shared-name", ["right-item"], QueueDirection.Back, false, cancellationToken);
        var aggregated = await ReadInventory(subscription, 3, cancellationToken);

        _ = await Assert.That(aggregated.Queues).Count().IsEqualTo(3);
        _ = await Assert.That(aggregated.Revision).IsGreaterThan(initial.Revision);
        _ = await Assert.That(string.Join(',', aggregated.Queues.Select(queue =>
            $"{queue.OwnerAgentSessionId}/{queue.Name}")))
            .IsEqualTo("left-agent/shared-name,right-agent/shared-name,root-agent/root-work");

        left.Dispose();
        var removed = await ReadInventory(subscription, 2, cancellationToken);
        _ = await Assert.That(removed.Revision).IsGreaterThan(aggregated.Revision);
        _ = await Assert.That(removed.Queues.Any(queue => queue.OwnerAgentSessionId == "left-agent")).IsFalse();
    }

    [Test]
    public async Task Root_registration_adopts_legacy_monitored_queues(CancellationToken cancellationToken)
    {
        var resources = Resources("legacy-adoption");
        _ = Directory.CreateDirectory(resources.QueueDirectory);
        var path = Path.Combine(resources.QueueDirectory, "legacy-work.jsonl");
        await File.WriteAllTextAsync(
            path,
            "{\"name\":\"legacy-work\",\"description\":\"legacy\",\"monitored\":true,\"delivery_id\":\"legacy-delivery\"}\n\"item\"\n",
            cancellationToken);

        using var catalog = new AgentQueueCatalog(resources);
        using var root = catalog.Register(AgentIdentity.Main("root-agent", "root"));
        var adopted = root.Get("legacy-work");
        var persisted = await File.ReadAllTextAsync(path, cancellationToken);

        _ = await Assert.That(adopted.Monitored).IsTrue();
        _ = await Assert.That(adopted.Size).IsEqualTo(1);
        _ = await Assert.That(string.Join(',', root.Local.ListenerSessionIds("legacy-work")))
            .IsEqualTo("root-agent");
        _ = await Assert.That(persisted).Contains("\"listener_session_ids\":[\"root-agent\"]");
        _ = await Assert.That(persisted).Contains("\"delivery_listener_session_id\":\"root-agent\"");
        _ = await Assert.That(persisted).DoesNotContain("\"monitored\":true");
    }

    private static async Task<QueueInventorySnapshot> ReadInventory(
        QueueInventorySubscription subscription,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var snapshot = await subscription.Reader.ReadAsync(cancellationToken);
            if (snapshot.Queues.Count == expectedCount)
            {
                return snapshot;
            }
        }
    }

    private static AgentIdentity Child(
        string sessionId,
        string parentSessionId,
        string parentName,
        string name,
        int depth) =>
        AgentIdentity.Child(sessionId, parentSessionId, parentName, name, depth, AgentScope.Empty);

    private static QueueNotFoundException CaptureNotFound(Action action)
    {
        try
        {
            action();
        }
        catch (QueueNotFoundException failure)
        {
            return failure;
        }

        throw new InvalidOperationException("Expected queue lookup to fail.");
    }

    private static bool TryCreate(AgentQueues queues, string name, ManualResetEventSlim start)
    {
        start.Wait();
        try
        {
            _ = queues.Create(name, string.Empty);
            return true;
        }
        catch (QueueAlreadyExistsException)
        {
            return false;
        }
    }

    private UserSessionResources Resources(string sessionId)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        return new UserSessionResources(
            new StatePaths(
                Path.Combine(_root, "state"),
                Path.Combine(_root, "config"),
                Path.Combine(_root, "data")),
            UserSessionId.Parse(sessionId),
            ProjectWorkspace.FromLaunchDirectory(workspace));
    }
}
