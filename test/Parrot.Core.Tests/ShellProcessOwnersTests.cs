using Parrot.Agent;
using Parrot.Context;
using Parrot.Diagnostics;
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
    public async Task Owners_isolate_names_inventory_and_settlement(
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
        await using var firstAgent = CreateAgent("agent-1", model, events, repository, resources.AgentScratch("agent-1").BlobDirectory, lifetime.Token);
        await using var secondAgent = CreateAgent("agent-2", model, events, repository, resources.AgentScratch("agent-2").BlobDirectory, lifetime.Token);
        await using var first = new ShellProcessOwner(
            AgentIdentity.Main(firstAgent.SessionId, firstAgent.Name, TestModels.PromptTemplates),
            resources,
            new AgentPathEnvironment(resources, resources.AgentScratch(firstAgent.SessionId)),
            new ProcessRunner(CreateSandboxPassThrough()),
            TestDiagnosticLog.Instance,
            lifetime.Token);
        await using var second = new ShellProcessOwner(
            AgentIdentity.Main(secondAgent.SessionId, secondAgent.Name, TestModels.PromptTemplates),
            resources,
            new AgentPathEnvironment(resources, resources.AgentScratch(secondAgent.SessionId)),
            new ProcessRunner(CreateSandboxPassThrough()),
            TestDiagnosticLog.Instance,
            lifetime.Token);
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
        var observations = first.Active().Concat(second.Active()).ToArray();
        _ = await Assert.That(string.Join('|', observations.Select(item => item.Id)))
            .IsEqualTo("agent-1/first-only|agent-1/shared|agent-2/shared");
        _ = await Assert.That(string.Join('|', observations.Select(item => item.Name)))
            .IsEqualTo("first-only|shared|shared");

        _ = await Assert.That(first.CaptureInventory().Processes.Count).IsEqualTo(2);
        _ = await Assert.That(second.CaptureInventory().Processes).HasSingleItem();
        var settlement = first.Settle();
        _ = await Assert.That(ReferenceEquals(first.Settle(), settlement)).IsTrue();
        _ = await Assert.That(() => first.StartUnattributed("late", "true", ProcessEnvironmentOverrides.Empty, firstAgent, security, ShellProcessTerminalMode.Pipe))
            .Throws<InvalidOperationException>();
        await settlement.WaitAsync(cancellationToken);
        await first.DisposeAsync();
        await first.DisposeAsync();
        _ = await Assert.That(first.CaptureInventory().Removed).IsTrue();
        _ = await Assert.That(lifetime.IsCancellationRequested).IsFalse();
        _ = await Assert.That(secondProcess.Completed).IsFalse();
        _ = await Assert.That(second.CaptureInventory().Processes).HasSingleItem();
        await second.Settle().WaitAsync(cancellationToken);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Owner_settles_completed_process_with_or_without_committed_result(
        bool commitResult,
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
        await using var agent = CreateAgent("agent-1", model, events, repository, resources.AgentScratch("agent-1").BlobDirectory, lifetime.Token);
        await using var owner = new ShellProcessOwner(
            AgentIdentity.Main(agent.SessionId, agent.Name, TestModels.PromptTemplates),
            resources,
            new AgentPathEnvironment(resources, resources.AgentScratch(agent.SessionId)),
            new ProcessRunner(CreateSandboxPassThrough()),
            TestDiagnosticLog.Instance,
            lifetime.Token);
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
        if (commitResult)
        {
            _ = await process.Wait(yieldAfter: null, cancellationToken);
            _ = await Assert.That(owner.Active()).IsEmpty();
        }

        await owner.Settle().WaitAsync(cancellationToken);
        _ = await Assert.That(lifetime.IsCancellationRequested).IsFalse();
        _ = await Assert.That(owner.CaptureInventory().CompletedProcesses).HasSingleItem();
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
        await using var agent = CreateAgent("agent-1", model, events, repository, resources.AgentScratch("agent-1").BlobDirectory, lifetime.Token);
        await using var owner = new ShellProcessOwner(
            AgentIdentity.Main(agent.SessionId, agent.Name, TestModels.PromptTemplates),
            resources,
            new AgentPathEnvironment(resources, resources.AgentScratch(agent.SessionId)),
            new ProcessRunner(CreateSandboxPassThrough()),
            TestDiagnosticLog.Instance,
            lifetime.Token);
        var completionMarker = Path.Combine(_workspace, "complete");
        var process = owner.StartPipe(
            "visible-from-start",
            $"while [ ! -f '{completionMarker}' ]; do sleep 0.01; done",
            "call-id",
            ProcessEnvironmentOverrides.Empty,
            agent,
            SecurityProfile.Compose(readOnly: false, [], [], []));

        using var subscription = owner.SubscribeInventory();
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

        using var resumed = owner.SubscribeInventory();
        var resumedSnapshot = await resumed.Reader.ReadAsync(cancellationToken);
        _ = await Assert.That(resumedSnapshot.CompletedProcesses).HasSingleItem();
        _ = await Assert.That(resumedSnapshot.CompletedProcesses[0]).IsEqualTo(completed.CompletedProcesses[0]);

        var laterProcess = owner.StartPipe(
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
        await owner.Settle();
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
        await using var agent = CreateAgent("agent-1", model, events, repository, resources.AgentScratch("agent-1").BlobDirectory, lifetime.Token);
        await using var owner = new ShellProcessOwner(
            AgentIdentity.Main(agent.SessionId, agent.Name, TestModels.PromptTemplates),
            resources,
            new AgentPathEnvironment(resources, resources.AgentScratch(agent.SessionId)),
            new ProcessRunner(CreateSandboxPassThrough()),
            TestDiagnosticLog.Instance,
            lifetime.Token);
        var completionMarker = Path.Combine(_workspace, "fault");
        var command = $"while [ ! -f '{completionMarker}' ]; do sleep 0.01; done; "
            + "awk 'BEGIN { for (i = 0; i < 1000000; i++) printf \"x\" }'";
        var process = owner.StartPipe(
            "faulted",
            command,
            "call-id",
            ProcessEnvironmentOverrides.Empty,
            agent,
            SecurityProfile.Compose(readOnly: false, [], [], []));

        using var subscription = owner.SubscribeInventory();
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

        using var resumed = owner.SubscribeInventory();
        var resumedSnapshot = await resumed.Reader.ReadAsync(cancellationToken);
        _ = await Assert.That(resumedSnapshot.CompletedProcesses).HasSingleItem();
        _ = await Assert.That(resumedSnapshot.CompletedProcesses[0]).IsEqualTo(completed.CompletedProcesses[0]);

        await owner.Settle();
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
            "agent-id",
            "inventory",
            7,
            false,
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
            new ShellProcessInventorySnapshot("agent-id", "empty", 0, false, [], [])).Single();
        _ = await Assert.That(empty.ShellProcessSnapshot.ChunkCount).IsEqualTo(1U);
        _ = await Assert.That(empty.ShellProcessSnapshot.Processes).IsEmpty();
        _ = await Assert.That(empty.ShellProcessSnapshot.CompletedProcesses).IsEmpty();
    }

    [Test]
    [Arguments(0)]
    [Arguments(7)]
    [Arguments(-1)]
    [Arguments(-2)]
    [Arguments(-3)]
    public async Task Diagnostics_follow_yielded_process_to_actual_completion_without_payloads(
        int exitCode,
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
            new StatePaths(Path.Combine(_workspace, "state"), Path.Combine(_workspace, "config"), Path.Combine(_workspace, "data")),
            UserSessionId.Parse($"session-{Guid.NewGuid():n}"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));
        using var diagnostics = FileDiagnosticLog.OpenSession(resources, "test", TextWriter.Null, TimeProvider.System);
        await using var agent = CreateAgent("agent-1", model, events, repository, resources.AgentScratch("agent-1").BlobDirectory, lifetime.Token);
        await using var owner = new ShellProcessOwner(
            AgentIdentity.Main(agent.SessionId, agent.Name, TestModels.PromptTemplates),
            resources,
            new AgentPathEnvironment(resources, resources.AgentScratch(agent.SessionId)),
            new ProcessRunner(exitCode == -3 ? string.Empty : CreateSandboxPassThrough()),
            diagnostics,
            lifetime.Token);
        if (exitCode == -3)
        {
            _ = await Assert.That(() => owner.StartUnattributed(
                "secret-process-name",
                "secret-command",
                ProcessEnvironmentOverrides.Empty,
                agent,
                SecurityProfile.Compose(readOnly: false, [], [], []),
                ShellProcessTerminalMode.Pipe)).Throws<SandboxUnavailableException>();
            var failedLog = await File.ReadAllTextAsync(resources.LogPath, cancellationToken);
            _ = await Assert.That(failedLog).Contains("event=\"start\"")
                .And.Contains("event=\"completed\"")
                .And.Contains("outcome=\"failed\"")
                .And.DoesNotContain("secret-");
            return;
        }

        var process = owner.StartUnattributed(
            "secret-process-name",
            exitCode < 0 ? "exec sleep 30 # secret-command" : $"sleep 0.2; printf secret-output; exit {exitCode}",
            ProcessEnvironmentOverrides.Empty,
            agent,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            ShellProcessTerminalMode.Pipe);
        var yielded = await process.Wait(TimeSpan.Zero, cancellationToken);
        _ = await Assert.That(yielded.Running).IsTrue();
        if (exitCode == -2)
        {
            await lifetime.CancelAsync();
        }
        else if (exitCode < 0)
        {
            await owner.Claim(process.Name).SendSignal(new ProcessSignal(15), cancellationToken);
        }

        while (!process.Completed)
        {
            await Task.Delay(10, cancellationToken);
        }

        await lifetime.CancelAsync();
        await owner.Settle();
        var log = await File.ReadAllTextAsync(resources.LogPath, cancellationToken);
        _ = await Assert.That(log).Contains("event=\"start\"")
            .And.Contains("event=\"yield\"")
            .And.Contains("event=\"completed\"")
            .And.Contains(process.State.ProcessId)
            .And.Contains(exitCode == 0 ? "outcome=\"succeeded\"" : exitCode == -2 ? "outcome=\"cancelled\"" : "outcome=\"failed\"")
            .And.DoesNotContain("secret-");
        if (exitCode == -1)
        {
            _ = await Assert.That(log).Contains("event=\"signal\"");
        }
    }

    private IAgentSession CreateAgent(
        string sessionId,
        ProviderModel model,
        IEventBroker events,
        IEventRepository repository,
        string blobDirectory,
        CancellationToken lifetime)
    {
        var identity = AgentIdentity.Main(sessionId, sessionId, TestModels.PromptTemplates);
        using var dependencies = TestModels.Dependencies(identity, events, repository, lifetime);
        return new AgentSession(identity, AgentSessionParentScope.Root(), new ModelSelector(model.Selector), TestModels.Route(model), events, repository, [], TestModels.MaterializePrompt(identity, _workspace, _workspace), new ToolOutputBlobStore(blobDirectory), new AgentOutputFile(blobDirectory), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(TestDiagnosticLog.Instance, "agent-test", null), new ContextCadence(), TestModels.PromptTemplates, dependencies.ChildQuestions, dependencies.ExitReminder, dependencies.Profile, new TestCompletionCallbacksFixture(dependencies.ChildQuestions, dependencies.ActiveWorkReminder, dependencies.ExitReminder, repository, events).Callbacks, new SecurityProfileTestFixture(SecurityProfile.Compose(readOnly: false, [], [], [])).Security, dependencies.Status, new AgentSessionActivity(TimeProvider.System), TestDiagnosticLog.Instance, lifetime);
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
