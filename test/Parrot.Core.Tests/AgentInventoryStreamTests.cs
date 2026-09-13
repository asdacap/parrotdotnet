using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;
using Parrot.Diagnostics;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.Queues;
using Parrot.Skills;
using Parrot.State;
using Parrot.Store;
using Parrot.Web;

namespace Parrot.Core.Tests;

internal sealed class AgentInventoryStreamTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Listener_captures_admitted_owners_and_observes_later_admission_removal_and_reconnection(
        bool createBeforeListening,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var directory = Path.Combine(Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("n"));
        using var diagnostics = InventoryFixture.CreateDiagnostics(directory);
        await using var fixture = await InventoryFixture.Create(directory, diagnostics, false);
        var session = fixture.Session;
        var root = session.Registry.SnapshotScopes().Single();
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

        await using var listener = session.Listen(timeout.Token).GetAsyncEnumerator(timeout.Token);
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
        await using var reconnected = session.Listen(reconnectCancellation.Token).GetAsyncEnumerator(reconnectCancellation.Token);
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
        using var diagnostics = InventoryFixture.CreateDiagnostics(directory);
        await using var fixture = await InventoryFixture.Create(directory, diagnostics, true);
        var root = fixture.Session.Registry.SnapshotScopes().Single();
        await using var child = fixture.CreateChild(root, "held-claim");
        _ = await Assert.That(root.ChildRegistry.TryAdd(child)).IsTrue();
        var owner = disposeRoot ? root : child;
        _ = owner.GetService<IAgentQueues>().Create("queued", "held claim");
        _ = await owner.GetService<IAgentQueues>().Push("queued", ["item"], QueueDirection.Back, false, timeout.Token);
        var processOwner = owner.Processes;
        var queueOwner = owner.GetService<IAgentQueues>();
        var process = processOwner.StartUnattributed("held", "sleep 300", ProcessEnvironmentOverrides.Empty, owner.Session, fixture.Session.Mode.Profile.SecurityProfile, ShellProcessTerminalMode.Pipe);
        using var queues = owner.GetService<IAgentQueues>().SubscribeInventory();
        using var processes = owner.Processes.SubscribeInventory();
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
        _ = await Assert.That(ReferenceEquals(owner.Processes, processOwner)).IsTrue();
        _ = await Assert.That(ReferenceEquals(owner.GetService<IAgentQueues>(), queueOwner)).IsTrue();
        _ = await Assert.That(queueRevisions.Last().Removed).IsTrue();
        _ = await Assert.That(queueRevisions.Last().Queues).IsEmpty();
        _ = await Assert.That(queueRevisions.Last().InventoryInstanceId).IsEqualTo(initialQueues.InventoryInstanceId);
        _ = await Assert.That(processRevisions.Last().Removed).IsTrue();
        _ = await Assert.That(processRevisions.Last().Processes).IsEmpty();
        _ = await Assert.That(processRevisions.Last().InventoryInstanceId).IsEqualTo(initialProcesses.InventoryInstanceId);
        _ = await Assert.That(owner.GetService<IAgentQueues>().CaptureInventory().Removed).IsTrue();
        _ = await Assert.That(owner.Processes.CaptureInventory().Removed).IsTrue();
        _ = await Assert.That(child.Processes.CaptureInventory().Removed).IsTrue();
        _ = await Assert.That(child.GetService<IAgentQueues>().CaptureInventory().Removed).IsTrue();
        _ = await Assert.That(() => owner.Processes.StartUnattributed("late", "true", ProcessEnvironmentOverrides.Empty, owner.Session, fixture.Session.Mode.Profile.SecurityProfile, ShellProcessTerminalMode.Pipe))
            .Throws<InvalidOperationException>();
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

    private sealed class InventoryFixture(
        string directory,
        Configuration configuration,
        ProviderModel model,
        IUserSession session) : IAsyncDisposable
    {
        public IUserSession Session { get; } = session;

        public static DiagnosticLogs CreateDiagnostics(string directory) => new(
            new StatePaths(Path.Combine(directory, "state"), Path.Combine(directory, "config"), Path.Combine(directory, "data")),
            FileDiagnosticLog.CreateInstanceId(),
            TextWriter.Null,
            TimeProvider.System);

        public static async Task<InventoryFixture> Create(string directory, DiagnosticLogs diagnostics, bool passThroughSandbox)
        {
            _ = Directory.CreateDirectory(directory);
            var paths = new StatePaths(Path.Combine(directory, "state"), Path.Combine(directory, "config"), Path.Combine(directory, "data"));
            try
            {
                var configuration = Configuration.Load(paths.ConfigFile, paths.PredefinedConfigFile);
                ILLMProvider provider = new UnusedProvider();
                var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
                var router = TestModels.Route(model);
                var profiles = new ProfileRegistry(configuration.Profiles, configuration.SandboxRules, [], configuration.DisabledTools);
                var modes = new ModeRegistry(profiles, configuration.DefaultProfile);
                var runner = ProcessRunner.Locate(ExecutableLocator.Capture());
                if (passThroughSandbox && OperatingSystem.IsLinux())
                {
                    var sandboxPath = Path.Combine(directory, "sandbox");
                    var script = "#!/bin/sh\nwhile [ \"$1\" != \"--\" ]; do\n"
                        + "  if [ \"$1\" = \"--chdir\" ]; then shift; cd \"$1\" || exit; "
                        + "elif [ \"$1\" = \"--setenv\" ]; then export \"$2=$3\"; shift 2; fi\n"
                        + "  shift\ndone\nshift\nexec \"$@\"\n";
                    await File.WriteAllTextAsync(sandboxPath, script);
                    File.SetUnixFileMode(sandboxPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    runner = new ProcessRunner(sandboxPath);
                }

                var source = new AgentSessionFactorySource(
                    runner,
                    new Compactor(90, 30, 60_000, 1024, configuration.PromptTemplates),
                    WebFetcher.Create(new PublicWebAddressPolicy()),
                    configuration.ToolDefinitions,
                    configuration.AgentTasks,
                    configuration.RequestLimits,
                    configuration.ReadOnlyExecCommandPrefixes,
                    router,
                    new CompositeSystemPromptProvider("test:inventory", []),
                    configuration.PromptTemplates,
                    static (arguments, scope) => new AgentSessionComposition(arguments, scope));
                var factory = new UserSessionFactory(source, modes, configuration.PromptTemplates, profiles, new SkillCatalogFactory(configuration, directory, Path.Combine(directory, "skills")), TimeSpan.FromSeconds(30), TimeProvider.System);
                var store = new SessionStore(paths, directory, "host", factory, router, modes, diagnostics);
                return new InventoryFixture(directory, configuration, model, await store.Open(router.Resolve(model.Selector)));
            }
            catch
            {
                Directory.Delete(directory, recursive: true);
                throw;
            }
        }

        public IAgentSessionScope CreateChild(IAgentSessionScope parent, string name) => Session.Registry.CreateChildScope(
            AgentIdentity.Child(name, parent.Session.SessionId, parent.Session.Name, name, 1, AgentScope.Empty(configuration.PromptTemplates), configuration.PromptTemplates),
            AgentSessionParentLink.Child(parent, AgentCompletionDeliveryPolicy.RetainedOnly),
            new ModelSelector(model.Selector),
            Session.Mode,
            Session.Mode.Profile.SecurityProfile,
            Session.Status,
            Session.Registry.InitializeChildHistory(parent.Session.SessionId, name, new HistoryForkBoundary.AfterCompletedHistory(), HistoryForkSelection.Parse("empty")),
            Session.Lifetime);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Session.DisposeAsync();
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
