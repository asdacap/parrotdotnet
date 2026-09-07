using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Security;
using Parrot.State;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class ShellProcessOwnersTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "parrot-shell-process-owner-tests", Guid.NewGuid().ToString("n"));

    public ShellProcessOwnersTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Test]
    public async Task Owners_isolate_names_and_coordinator_qualifies_observations(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var lifetime = new CancellationTokenSource();
        using var events = new EventBroker();
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".state"),
                Path.Combine(_workspace, ".config"),
                Path.Combine(_workspace, ".data")),
            UserSessionId.Parse($"session-{Guid.NewGuid():n}"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        using var coordinator = new ShellProcessOwners(
            resources,
            new ProcessRunner(CreateSandboxPassThrough()),
            lifetime.Token);
        await using var firstAgent = CreateAgent("agent-1", model, events, repository, resources.AgentScratch("agent-1").BlobDirectory, lifetime.Token);
        await using var secondAgent = CreateAgent("agent-2", model, events, repository, resources.AgentScratch("agent-2").BlobDirectory, lifetime.Token);
        var first = coordinator.Prepare(firstAgent.SessionId);
        var second = coordinator.Prepare(secondAgent.SessionId);
        coordinator.Register(first);
        coordinator.Register(second);
        var security = SecurityProfile.Compose(readOnly: false, [], [], []);

        var firstProcess = first.Start(
            "shared",
            "sleep 30",
            "call-id",
            ProcessEnvironmentOverrides.Empty,
            firstAgent,
            security,
            ShellProcessTerminalMode.Pipe);
        var secondProcess = second.Start(
            "shared",
            "sleep 30",
            "call-id",
            ProcessEnvironmentOverrides.Empty,
            secondAgent,
            security,
            ShellProcessTerminalMode.Pipe);
        var firstOnlyProcess = first.Start(
            "first-only",
            "sleep 30",
            "call-id",
            ProcessEnvironmentOverrides.Empty,
            firstAgent,
            security,
            ShellProcessTerminalMode.Pipe);
        _ = await firstProcess.Wait(TimeSpan.Zero, cancellationToken);
        _ = await secondProcess.Wait(TimeSpan.Zero, cancellationToken);
        _ = await firstOnlyProcess.Wait(TimeSpan.Zero, cancellationToken);

        _ = await Assert.That(() => second.Claim("first-only"))
            .Throws<InvalidOperationException>();
        var observations = coordinator.Active();
        _ = await Assert.That(string.Join('|', observations.Select(item => item.Id)))
            .IsEqualTo("agent-1/first-only|agent-1/shared|agent-2/shared");
        _ = await Assert.That(string.Join('|', observations.Select(item => item.Name)))
            .IsEqualTo("first-only|shared|shared");

        await lifetime.CancelAsync();
        await coordinator.Settle();
        _ = await Assert.That(() => coordinator.Prepare("agent-3"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Owner_keeps_completed_process_active_until_its_result_is_committed(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var lifetime = new CancellationTokenSource();
        using var events = new EventBroker();
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".state"),
                Path.Combine(_workspace, ".config"),
                Path.Combine(_workspace, ".data")),
            UserSessionId.Parse($"session-{Guid.NewGuid():n}"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        using var coordinator = new ShellProcessOwners(
            resources,
            new ProcessRunner(CreateSandboxPassThrough()),
            lifetime.Token);
        await using var agent = CreateAgent("agent-1", model, events, repository, resources.AgentScratch("agent-1").BlobDirectory, lifetime.Token);
        var owner = coordinator.Prepare(agent.SessionId);
        coordinator.Register(owner);
        var process = owner.Start(
            "completed-but-uncommitted",
            "true",
            "call-id",
            ProcessEnvironmentOverrides.Empty,
            agent,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            ShellProcessTerminalMode.Pipe);

        while (!process.Completed)
        {
            await Task.Delay(10, cancellationToken);
        }

        _ = await Assert.That(owner.Active()).HasSingleItem();
        _ = await process.Wait(yieldAfter: null, cancellationToken);
        _ = await Assert.That(owner.Active()).IsEmpty();

        await coordinator.Settle();
    }

    [Test]
    public async Task Inventory_tracks_process_from_start_until_completion(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var lifetime = new CancellationTokenSource();
        using var events = new EventBroker();
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".state"),
                Path.Combine(_workspace, ".config"),
                Path.Combine(_workspace, ".data")),
            UserSessionId.Parse($"session-{Guid.NewGuid():n}"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        using var coordinator = new ShellProcessOwners(
            resources,
            new ProcessRunner(CreateSandboxPassThrough()),
            lifetime.Token);
        await using var agent = CreateAgent("agent-1", model, events, repository, resources.AgentScratch("agent-1").BlobDirectory, lifetime.Token);
        var owner = coordinator.Prepare(agent.SessionId);
        coordinator.Register(owner);
        var completionMarker = Path.Combine(_workspace, "complete");
        var process = owner.Start(
            "visible-from-start",
            $"while [ ! -f '{completionMarker}' ]; do sleep 0.01; done",
            "call-id",
            ProcessEnvironmentOverrides.Empty,
            agent,
            SecurityProfile.Compose(readOnly: false, [], [], []));

        using var subscription = coordinator.SubscribeInventory();
        var initial = await subscription.Reader.ReadAsync(cancellationToken);

        _ = await Assert.That(initial.Processes.Count).IsEqualTo(1);
        _ = await Assert.That(initial.Processes[0].ProcessId).IsEqualTo(process.State.ProcessId);
        var yielded = await process.Wait(TimeSpan.Zero, cancellationToken);
        var yieldedProcess = yielded.YieldedProcess
            ?? throw new InvalidOperationException("The running process did not yield.");
        _ = await Assert.That(yieldedProcess.VisibleRevision).IsEqualTo(initial.Revision);

        var claimed = owner.Claim(process.Name);
        await File.WriteAllTextAsync(completionMarker, string.Empty, cancellationToken);
        var result = await claimed.Wait(null, cancellationToken);
        var completed = await subscription.Reader.ReadAsync(cancellationToken);

        _ = await Assert.That(completed.Revision).IsGreaterThan(initial.Revision);
        _ = await Assert.That(completed.Processes).IsEmpty();
        _ = await Assert.That(completed.CompletedProcesses).HasSingleItem();
        _ = await Assert.That(completed.CompletedProcesses[0].ProcessId).IsEqualTo(process.State.ProcessId);
        _ = await Assert.That(completed.CompletedProcesses[0].ElapsedMilliseconds)
            .IsEqualTo(result.Result?.ElapsedMilliseconds);

        using var resumed = coordinator.SubscribeInventory();
        var resumedSnapshot = await resumed.Reader.ReadAsync(cancellationToken);
        _ = await Assert.That(resumedSnapshot.CompletedProcesses).HasSingleItem();
        _ = await Assert.That(resumedSnapshot.CompletedProcesses[0]).IsEqualTo(completed.CompletedProcesses[0]);

        var laterProcess = owner.Start(
            "later",
            "sleep 30",
            "later-call",
            ProcessEnvironmentOverrides.Empty,
            agent,
            SecurityProfile.Compose(readOnly: false, [], [], []));
        var laterSnapshot = await resumed.Reader.ReadAsync(cancellationToken);
        _ = await Assert.That(laterSnapshot.Processes).HasSingleItem();
        _ = await Assert.That(laterSnapshot.Processes[0].ProcessId).IsEqualTo(laterProcess.State.ProcessId);
        _ = await Assert.That(laterSnapshot.CompletedProcesses).HasSingleItem();
        _ = await Assert.That(laterSnapshot.CompletedProcesses[0]).IsEqualTo(completed.CompletedProcesses[0]);

        await lifetime.CancelAsync();
        await coordinator.Settle();
    }

    [Test]
    public async Task Faulted_process_completion_retains_a_tombstone_without_elapsed_time(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var lifetime = new CancellationTokenSource();
        using var events = new EventBroker();
        using var database = SessionDatabase.Open(":memory:");
        var repository = new EventRepository(database);
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        var resources = new UserSessionResources(
            new StatePaths(
                Path.Combine(_workspace, ".state"),
                Path.Combine(_workspace, ".config"),
                Path.Combine(_workspace, ".data")),
            UserSessionId.Parse($"session-{Guid.NewGuid():n}"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        using var coordinator = new ShellProcessOwners(
            resources,
            new ProcessRunner(CreateSandboxPassThrough()),
            lifetime.Token);
        await using var agent = CreateAgent("agent-1", model, events, repository, resources.AgentScratch("agent-1").BlobDirectory, lifetime.Token);
        var owner = coordinator.Prepare(agent.SessionId);
        coordinator.Register(owner);
        var completionMarker = Path.Combine(_workspace, "fault");
        var command = $"while [ ! -f '{completionMarker}' ]; do sleep 0.01; done; "
            + "awk 'BEGIN { for (i = 0; i < 1000000; i++) printf \"x\" }'";
        var process = owner.Start(
            "faulted",
            command,
            "call-id",
            ProcessEnvironmentOverrides.Empty,
            agent,
            SecurityProfile.Compose(readOnly: false, [], [], []));

        using var subscription = coordinator.SubscribeInventory();
        var active = await subscription.Reader.ReadAsync(cancellationToken);
        _ = await Assert.That(active.Processes).HasSingleItem();
        var claimed = process;
        var blobDirectory = resources.AgentScratch(agent.SessionId).BlobDirectory;
        var movedBlobDirectory = blobDirectory + "-moved";
        Directory.Move(blobDirectory, movedBlobDirectory);
        await File.WriteAllTextAsync(blobDirectory, string.Empty, cancellationToken);
        await File.WriteAllTextAsync(completionMarker, string.Empty, cancellationToken);

        var failure = await Assert.That(async () => await claimed.Wait(null, cancellationToken))
            .Throws<IOException>();
        var completed = await subscription.Reader.ReadAsync(cancellationToken);

        _ = await Assert.That(failure?.Message).Contains("blob");
        _ = await Assert.That(completed.Processes).IsEmpty();
        _ = await Assert.That(completed.CompletedProcesses).HasSingleItem();
        _ = await Assert.That(completed.CompletedProcesses[0].ProcessId).IsEqualTo(process.State.ProcessId);
        _ = await Assert.That(completed.CompletedProcesses[0].ElapsedMilliseconds).IsNull();

        using var resumed = coordinator.SubscribeInventory();
        var resumedSnapshot = await resumed.Reader.ReadAsync(cancellationToken);
        _ = await Assert.That(resumedSnapshot.CompletedProcesses).HasSingleItem();
        _ = await Assert.That(resumedSnapshot.CompletedProcesses[0]).IsEqualTo(completed.CompletedProcesses[0]);

        await coordinator.Settle();
    }

    [Test]
    public async Task Inventory_protocol_chunks_active_and_completed_records_deterministically()
    {
        var active = new ActiveShellProcessState(
            "active-b",
            "build",
            "dotnet build",
            "call-id",
            "agent-id",
            "main",
            string.Empty,
            string.Empty,
            0,
            System.Diagnostics.Stopwatch.GetTimestamp());
        var inventory = new ShellProcessInventorySnapshot(
            "inventory",
            7,
            [active],
            [
                new CompletedShellProcessState("completed-a", 5_001),
                new CompletedShellProcessState("completed-c", null),
            ]);

        var chunks = ShellProcessInventoryProtocol.Convert(inventory).ToArray();

        _ = await Assert.That(chunks).Count().IsEqualTo(3);
        _ = await Assert.That(string.Join(',', chunks.Select(static chunk => chunk.ShellProcessSnapshot.ChunkIndex)))
            .IsEqualTo("0,1,2");
        _ = await Assert.That(chunks.All(static chunk => chunk.ShellProcessSnapshot.ChunkCount == 3)).IsTrue();
        _ = await Assert.That(chunks[0].ShellProcessSnapshot.Processes[0].ProcessId).IsEqualTo("active-b");
        _ = await Assert.That(chunks[1].ShellProcessSnapshot.CompletedProcesses[0].ProcessId)
            .IsEqualTo("completed-a");
        _ = await Assert.That(chunks[1].ShellProcessSnapshot.CompletedProcesses[0].ElapsedMs).IsEqualTo(5_001);
        _ = await Assert.That(chunks[2].ShellProcessSnapshot.CompletedProcesses[0].ProcessId)
            .IsEqualTo("completed-c");
        _ = await Assert.That(chunks[2].ShellProcessSnapshot.CompletedProcesses[0].HasElapsedMs).IsFalse();

        var empty = ShellProcessInventoryProtocol.Convert(
            new ShellProcessInventorySnapshot("empty", 0, [], [])).Single();
        _ = await Assert.That(empty.ShellProcessSnapshot.ChunkCount).IsEqualTo(1U);
        _ = await Assert.That(empty.ShellProcessSnapshot.Processes).IsEmpty();
        _ = await Assert.That(empty.ShellProcessSnapshot.CompletedProcesses).IsEmpty();
    }

    [Test]
    public async Task Settling_atomically_rejects_new_owners()
    {
        using var lifetime = new CancellationTokenSource();
        using var coordinator = new ShellProcessOwners(
            new UserSessionResources(
                new StatePaths(
                    Path.Combine(_workspace, ".state"),
                    Path.Combine(_workspace, ".config"),
                    Path.Combine(_workspace, ".data")),
                UserSessionId.Parse($"session-{Guid.NewGuid():n}"),
                ProjectWorkspace.FromLaunchDirectory(_workspace)),
            new ProcessRunner(string.Empty),
            lifetime.Token);

        var settlement = coordinator.Settle();

        _ = await Assert.That(() => coordinator.Prepare("late"))
            .Throws<InvalidOperationException>();
        await settlement;
    }

    private IAgentSession CreateAgent(
        string sessionId,
        ProviderModel model,
        EventBroker events,
        EventRepository repository,
        string blobDirectory,
        CancellationToken lifetime)
    {
        var identity = AgentIdentity.Main(sessionId, sessionId, TestModels.PromptTemplates);
        using var dependencies = TestModels.Dependencies(identity, events, repository, lifetime);
        return new AgentSession(identity, AgentSessionParentScope.Root(), new ModelSelector(model.Selector), TestModels.Route(model), events, repository, [], TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, _workspace, _workspace), new ToolOutputBlobStore(blobDirectory), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(), new ContextCadence(), TestModels.PromptTemplates, dependencies.ChildQuestions, dependencies.ExitReminder, dependencies.Profile, new TestCompletionCallbacksFixture(dependencies.ChildQuestions, dependencies.ActiveWorkReminder, dependencies.ExitReminder, repository, events).Callbacks, new SecurityProfileTestFixture(SecurityProfile.Compose(readOnly: false, [], [], [])).Security, dependencies.Status, dependencies.Queues, new AgentSessionActivity(TimeProvider.System), lifetime);
    }

    private string CreateSandboxPassThrough()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        var path = Path.Combine(_workspace, $"sandbox-{Guid.NewGuid():n}");
        var script = "#!/bin/sh\nwhile [ \"$1\" != \"--\" ]; do\n"
            + "  if [ \"$1\" = \"--chdir\" ]; then shift; cd \"$1\" || exit; "
            + "elif [ \"$1\" = \"--setenv\" ]; then export \"$2=$3\"; shift 2; fi\n"
            + "  shift\ndone\nshift\nexec \"$@\"\n";
        File.WriteAllText(path, script);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
